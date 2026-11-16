using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.NotificationService;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.ApplicationTests.Dynamic.Approvals.ApprovalGraphTestKit;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>
/// 等價性（計畫 fg2 Task 12；流程圖設計 §14；比較口徑 J23）：同一組情境對 sequence 流程與 <see cref="ApprovalGraphConversion.FromSequence"/> 轉出的
/// graph 流程各跑一次，正規化後逐項比較關卡實例、任務、歷程、通知、實例與記錄狀態。
/// 兩邊用同一個測試資料庫裡的兩組單據模組（po 走 sequence、pg 走 graph），腳本逐步相同；正規化只去掉兩邊本來就不同的東西
/// （Id、Seq 的數值、時間、模組鍵與記錄 id、graph 才有的開始／結束／分支節點實例列）。
/// 每個情境都是 <c>[Fact]</c>：比較需要同一個測試裡拿到兩邊的結果，所以不用以 shape 為參數的 <c>[Theory]</c>。
/// </summary>
public class ApprovalGraphEquivalenceTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private sealed class Clock : IBusinessTime
    {
        public DateTime Now { get; set; } = T(4, 9);
        public DateOnly Today => DateOnly.FromDateTime(Now);
    }

    private readonly Clock _clock = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IBusinessTime>(_clock);
    }

    private static DateTime T(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0);

    // ───────── 比較框架 ─────────

    /// <summary>一邊（sequence 或 graph）的單據模組與腳本步驟紀錄（每次動作的結果也納入比較）。</summary>
    private sealed class Side(PurchaseRig r)
    {
        public PurchaseRig R { get; } = r;
        public List<string> Steps { get; } = [];
    }

    private sealed record Spec(string Suffix, int Priority, ApprovalDefinition Def);

    /// <summary>兩邊各建一組單據模組、種入流程（graph 邊用轉換）、跑同一個腳本，最後比較正規化的快照。</summary>
    private async Task Run(Func<PurchaseRig, List<Spec>> flows, Func<Side, Task> script, [CallerMemberName] string scenario = "")
    {
        var seq = await Arrange("po", flows, graph: false);
        var graph = await Arrange("pg", flows, graph: true);

        await script(seq);
        var expected = Snapshot(seq);   // graph 腳本開始前先取：共用的時鐘、代理設定不會讓 sequence 一側多出資料
        await script(graph);
        var actual = Snapshot(graph);
        Snapshot(seq).Should().Equal(expected, "graph 腳本不能動到 sequence 一側的資料");
        Db.Context.Set<ApprovalStageRun>().AsNoTracking().Any(x => x.NodeKind == ApprovalNodeKinds.Start && x.InstanceId != 0
            && Db.Context.Set<ApprovalInstance>().Any(i => i.Id == x.InstanceId && i.ModuleId == graph.R.ModuleId))
            .Should().BeTrue("graph 一側必須真的走圖引擎（有開始節點的節點實例），否則比較退化成 sequence 對 sequence");
        expected.Should().Contain(l => l.StartsWith("run"), "情境至少要產生關卡實例，否則比較沒有意義");
        actual.Should().Equal(expected, "sequence 與轉換後的 graph 流程在同一腳本下必須逐項相同");

        // 防空轉：快照裡一定要看得到這個情境想驗的東西，劇本改壞或兩邊都沒做事時不會默默通過
        if (scenario == nameof(Overdue_ReminderNotice))
            expected.Count(l => l.StartsWith("notice approvalOverdue user=mgrA")).Should().Be(2);
        foreach (var fragment in Expected[scenario])
            expected.Should().Contain(l => l.Contains(fragment), $"{scenario} 的快照要包含「{fragment}」");
    }

    private Task Run(Func<PurchaseRig, ApprovalDefinition> def, Func<Side, Task> script, [CallerMemberName] string scenario = "") =>
        Run(r => [new Spec("a", 1, def(r))], script, scenario);

    /// <summary>各情境快照必須出現的片段（兩邊相同之外的實質內容檢查）。</summary>
    private static readonly Dictionary<string, string[]> Expected = new()
    {
        [nameof(ThreeStages_MiddleSkipped_ApproveAll)] = ["code=s2 status=skipped skip=condition", "inst0 status=approved", "rec0 state=已確認"],
        [nameof(AllStagesSkipped_ApprovedAtSubmit)] = ["submit buyer1 => created:approved", "inst0 status=approved", "rec0 state=已確認"],
        [nameof(OnEmptySkip)] = ["code=s1 status=skipped skip=empty", "inst0 status=approved"],
        [nameof(ReturnPrevious_ThenApprove)] = ["action0 return", "status=returned", "inst0 status=approved"],
        [nameof(ReturnPrevious_ToAddSign_Requeued)] = ["action0 return", "code=- status=returned", "inst0 status=approved"],
        [nameof(RejectToInitiator_NoRejectToState)] = ["inst0 status=rejected", "action0 reject", "rec0 state=草稿"],
        [nameof(RejectToInitiator_RejectToState)] = ["inst0 status=rejected", "rec0 state=已退回"],
        [nameof(Withdraw_BeforeAnyAction)] = ["inst0 status=withdrawn", "notice approvalWithdrawn", "rec0 state=草稿"],
        [nameof(Cancel_ByManager)] = ["inst0 status=cancelled", "notice approvalCancelled", "action0 cancel"],
        [nameof(AddSign_Nested)] = ["addedBy=mgrA addSign=emp2", "addedBy=emp2 addSign=emp3", "inst0 status=approved"],
        [nameof(AddSign_TwoSiblingsSameStage)] = ["code=- status=approved skip=- mode=any addedBy=fin1 addSign=emp1", "addedBy=fin2 addSign=emp2", "inst0 status=approved"],
        [nameof(Transfer_ThenApprove)] = ["action0 transfer", "reason=transferred", "inst0 status=approved"],
        [nameof(Reassign_ThenApprove)] = ["action0 reassign", "status=reassigned", "inst0 status=approved"],
        [nameof(Delegated_ApproveByDelegate)] = ["reason=delegated", "onBehalf=fin1", "inst0 status=approved"],
        [nameof(Delegate_NotApplied_InitiatorIsDelegate)] = ["action0 not-delegated", "assignee=fin1 original=fin1 reason=resolved", "inst0 status=approved"],
        [nameof(Reroute_SkipConditionReopened)] = ["inst0 status=rerouted", "inst1 status=approved route=flow current=- restartedFrom=0", "notice approvalRerouted"],
        [nameof(Reroute_NoFlowMatches_Fallback)] = ["inst1 status=approved route=fallback", "reason=fallback"],
        [nameof(AllMode_TwoSeats)] = ["mode=all", "assignee=fin1", "assignee=fin2", "inst0 status=approved"],
        [nameof(FallbackApprovers_Used)] = ["action0 fallback", "assignee=gm original=gm reason=fallback", "inst0 status=approved"],
        [nameof(Fallback_NoFallbackApprovers_AdminUsed)] = ["action0 fallback", "assignee=admin original=admin reason=fallback", "inst0 status=approved"],
        [nameof(InitiatorPick_ThenApprove)] = ["picks={\"pick\":[\"fin2\",\"fin1\"]}", "assignee=fin2", "assignee=fin1", "inst0 status=approved"],
        [nameof(Reroute_NewFlowNeedsPick_NeedsResubmit)] = ["+resubmit", "inst0 status=rerouted", "\"kind\":\"pick\""],
        [nameof(AddSign_TwoTargets_AllMode)] = ["addSign=emp1,emp2", "mode=all addedBy=mgrA", "inst0 status=approved"],
        [nameof(Overdue_ReminderNotice)] = ["notice approvalOverdue user=mgrA", "notice approvalOverdue user=fin1", "inst0 status=pending"]
    };

    private async Task<Side> Arrange(string prefix, Func<PurchaseRig, List<Spec>> flows, bool graph)
    {
        var r = await BuildPurchase(prefix);
        foreach (var f in flows(r))
        {
            if (graph) await SeedGraphFlow(r, $"{prefix}-{f.Suffix}", r.Confirmed, f.Priority, ApprovalGraphConversion.FromSequence(f.Def));
            else await SeedFlow(r, $"{prefix}-{f.Suffix}", r.Confirmed, f.Priority, f.Def);
        }
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled), Plain(r.Draft, r.Returned)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module, r.Extra);
        return new Side(r);
    }

    // ───────── 腳本動作 ─────────

    private static string Describe(ServiceResult res) =>
        res.IsSuccess
            ? (res.IsCreated ? "created:" : "ok:") + res.Data switch
            {
                SubmitApprovalResponse p => p.Outcome,
                ApprovalActionResponse a => a.Outcome + (a.NeedsResubmit ? "+resubmit" : ""),
                _ => ""
            }
            : "fail:" + res.Message;

    /// <summary>執行一個動作並記下結果；預設要求成功（兩邊都成功才有比較價值）。</summary>
    private static async Task Step(Side s, string label, Task<ServiceResult> call, bool mustSucceed = true)
    {
        var res = await call;
        if (mustSucceed) res.IsSuccess.Should().BeTrue($"{label}：{Dump(res)}");
        s.Steps.Add($"{label} => {Describe(res)}");
    }

    private async Task<(long Record, long Instance)> Go(Side s, string user, decimal price = 20000, Dictionary<string, List<string>>? picks = null)
    {
        As(user);
        var po = await NewPo(s.R, price: price);
        var res = await Send(new SubmitApprovalRequest(po.Id, s.R.Confirmed, Rec(po.Id).Row.RowVersion, picks, null));
        s.Steps.Add($"submit {user} => {Describe(res)}");
        res.IsSuccess.Should().BeTrue(Dump(res));
        return (po.Id, ((SubmitApprovalResponse)res.Data!).InstanceId ?? 0);
    }

    /// <summary>每個動作之後記下實例狀態與目前關卡的正規化編號，中間狀態也納入比較。</summary>
    private void Mark(Side s, long id)
    {
        var inst = Inst(id);
        var runs = Db.Context.Set<ApprovalStageRun>().AsNoTracking().Where(x => x.InstanceId == id && (x.NodeKind == null || x.NodeKind == ApprovalNodeKinds.Approval))
            .OrderBy(x => x.Seq).Select(x => x.Id).ToList();
        s.Steps.Add($"  state {inst.Status} current={(inst.CurrentStageRunId is { } c ? runs.IndexOf(c).ToString() : "-")}");
    }

    private ApprovalTask Open(long id, string user) =>
        TasksOf(id).Single(t => t.AssigneeUserId == user && t.Status == ApprovalTaskStatus.Open);

    private async Task Approve(Side s, long id, string user)
    {
        As(user);
        await Step(s, $"approve {user}", Send(new ApproveTaskRequest(Open(id, user).Id, Inst(id).RowVersion, null, null)));
        Mark(s, id);
    }

    private Task AddSign(Side s, long id, string user, params string[] to) => AddSignMode(s, id, user, ApprovalModes.Any, to);

    private async Task AddSignMode(Side s, long id, string user, string mode, params string[] to)
    {
        As(user);
        await Step(s, $"add-sign {user}->{string.Join(",", to)} {mode}",
            Send(new AddSignTaskRequest(Open(id, user).Id, Inst(id).RowVersion, null, null, [.. to], mode)));
        Mark(s, id);
    }

    private async Task Return(Side s, long id, string user)
    {
        As(user);
        await Step(s, $"return {user}", Send(new RejectTaskRequest(Open(id, user).Id, Inst(id).RowVersion, "請重看", RejectTaskHandler.ToPrevious)));
        Mark(s, id);
    }

    private async Task Reject(Side s, long id, string user)
    {
        As(user);
        await Step(s, $"reject {user}", Send(new RejectTaskRequest(Open(id, user).Id, Inst(id).RowVersion, "請補報價", RejectTaskHandler.ToInitiator)));
        Mark(s, id);
    }

    private async Task Transfer(Side s, long id, string user, string to)
    {
        As(user);
        await Step(s, $"transfer {user}->{to}", Send(new TransferTaskRequest(Open(id, user).Id, Inst(id).RowVersion, to, null)));
        Mark(s, id);
    }

    private async Task Reassign(Side s, long id, string assignee, string to)
    {
        var taskId = Open(id, assignee).Id;
        As("admin");
        await Step(s, $"reassign {assignee}->{to}", Send(new ReassignTaskRequest(taskId, Inst(id).RowVersion, to, null)));
        Mark(s, id);
    }

    private async Task SetDelegate(string user, string to)
    {
        As(user);
        var res = await Send(new SaveMyApprovalDelegateRequest(to, new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 10)));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    /// <summary>簽核中由追加費用讓總額變動（允許的靜默更新，設計 §8.5）。</summary>
    private async Task AddExtra(Side s, long recordId, decimal amount)
    {
        As("buyer1");
        var res = await Send(new CreateRecordRequest(s.R.Extra, V((TitleId(s.R.Extra), "追加"), (s.R.ExtraRef, recordId), (s.R.ExtraAmount, amount))));
        res.IsCreated.Should().BeTrue(Dump(res));
    }

    /// <summary>這一邊目前的 pending 實例（重來之後是新實例）。</summary>
    private long Pending(Side s) =>
        Db.Context.Set<ApprovalInstance>().AsNoTracking().Single(i => i.ModuleId == s.R.ModuleId && i.Status == ApprovalInstanceStatus.Pending).Id;

    private async Task<NotificationListResponse> ReadNotices(string user)
    {
        As(user);
        var res = await Send(new GetNotificationsRequest(50));
        res.IsSuccess.Should().BeTrue(res.Message);
        Db.Context.ChangeTracker.Clear();
        return (NotificationListResponse)res.Data!;
    }

    // ───────── 正規化與快照（J23） ─────────

    /// <summary>歷程 DataJson 裡兩邊本來就不同的鍵（版本 id、流程 id、流程名稱，以及兩組模組各自的欄位 id）；遞迴移除。</summary>
    private static readonly string[] DataKeysDropped = ["flowVersionId", "flowId", "flowKey", "flowName", "versionId", "fieldId"];

    private static string NormalizeData(string? json, PurchaseRig r)
    {
        if (string.IsNullOrEmpty(json)) return "";
        var node = JsonNode.Parse(json);
        Drop(node);
        return (node?.ToJsonString() ?? "").Replace(r.Module, "@M");
    }

    private static void Drop(JsonNode? n)
    {
        switch (n)
        {
            case JsonObject o:
                foreach (var k in DataKeysDropped) o.Remove(k);
                foreach (var (_, v) in o.ToList()) Drop(v);
                break;
            case JsonArray a:
                foreach (var v in a) Drop(v);
                break;
        }
    }

    private static string Opt(PurchaseRig r, long? id) =>
        id == r.Draft ? "草稿" : id == r.Confirmed ? "已確認" : id == r.Cancelled ? "已取消" : id == r.Partial ? "部分進貨"
            : id == r.Received ? "已進貨" : id == r.Returned ? "已退回" : id == r.Changing ? "變更中" : "?" + id;

    private List<string> Snapshot(Side s)
    {
        var r = s.R;
        var set = Db.Context;
        set.ChangeTracker.Clear();
        var insts = set.Set<ApprovalInstance>().AsNoTracking().Where(i => i.ModuleId == r.ModuleId).OrderBy(i => i.Id).ToList();
        var instIdx = insts.Select((x, i) => (x.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var recIdx = insts.Select(i => i.RecordId).Distinct().Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var lines = s.Steps.Select(x => "step " + x).ToList();

        foreach (var inst in insts)
        {
            var i = instIdx[inst.Id];
            // 正規化編號：只取 NodeKind 為 null 或 approval 的列（含加簽），依 Seq 排序後編 0、1、2…
            var runs = set.Set<ApprovalStageRun>().AsNoTracking().Where(x => x.InstanceId == inst.Id).OrderBy(x => x.Seq).ToList();
            var approvalRuns = runs.Where(x => x.NodeKind is null or ApprovalNodeKinds.Approval).ToList();
            var norm = approvalRuns.Select((x, n) => (x.Id, n)).ToDictionary(x => x.Id, x => x.n);
            string N(long? runId) => runId is null ? "-" : norm.TryGetValue(runId.Value, out var n) ? n.ToString() : "?other";

            lines.Add($"inst{i} status={inst.Status} route={inst.RouteKind} current={N(inst.CurrentStageRunId)} " +
                      $"restartedFrom={(inst.RestartedFromInstanceId is { } f ? instIdx[f].ToString() : "-")} " +
                      $"rec=rec{recIdx[inst.RecordId]} picks={JsonSerializer.Serialize(inst.Picks.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value))}");

            foreach (var run in approvalRuns)
                lines.Add($"run{i}.{norm[run.Id]} code={run.StageCode ?? "-"} status={run.Status} skip={run.SkipReason ?? "-"} mode={run.Mode} " +
                          $"addedBy={run.AddedByUserId ?? "-"} addSign={string.Join(",", run.AddSignUserIds ?? [])}");

            var tasks = set.Set<ApprovalTask>().AsNoTracking().Where(t => t.InstanceId == inst.Id).OrderBy(t => t.Id).ToList()
                .OrderBy(t => norm.TryGetValue(t.StageRunId, out var n) ? n : int.MaxValue).ThenBy(t => t.Id).ToList();
            var taskPos = tasks.Select((t, n) => (t.Id, n)).ToDictionary(x => x.Id, x => x.n);
            foreach (var t in tasks)
                lines.Add($"task{i}.{taskPos[t.Id]} run={N(t.StageRunId)} seat={t.SeatKey} assignee={t.AssigneeUserId} original={t.OriginalUserId} " +
                          $"reason={t.Reason} status={t.Status}");

            foreach (var a in set.Set<ApprovalAction>().AsNoTracking().Where(x => x.InstanceId == inst.Id).OrderBy(x => x.Id))
                lines.Add($"action{i} {a.Action} run={N(a.StageRunId)} task={(a.TaskId is { } tid && taskPos.TryGetValue(tid, out var p) ? p.ToString() : "-")} " +
                          $"actor={a.ActorUserId} onBehalf={a.OnBehalfOfUserId ?? "-"} targets={string.Join(",", a.TargetUserIds ?? [])} data={NormalizeData(a.DataJson, r)}");
        }

        // 通知：SourceType = approval 而且 SourceId 屬於這個情境建出的實例（含重來建的新實例）；Link 中的模組鍵與記錄 id 換成占位字
        var ids = insts.Select(x => x.Id).ToList();
        foreach (var n in set.Set<Notification>().AsNoTracking().Where(x => x.SourceType == "approval" && x.SourceId != null && ids.Contains(x.SourceId.Value))
                     .OrderBy(x => x.Id).ToList())
        {
            var link = Regex.Replace(n.Link.Replace($"/{r.Module}/", "/@M/"), @"(?<=/)\d+(?=$|[/?#])",
                m => recIdx.TryGetValue(long.Parse(m.Value), out var k) ? $"@rec{k}" : m.Value);
            lines.Add($"notice {n.Kind} user={n.UserId} title={n.TitleSnapshot} link={link} source=inst{instIdx[n.SourceId!.Value]}");
        }

        foreach (var (recordId, k) in recIdx.OrderBy(x => x.Value)) lines.Add($"rec{k} state={Opt(r, StateOf(r, recordId))}");
        return lines;
    }

    // ───────── 流程定義 ─────────

    private static ApprovalDefinition ThreeStages(PurchaseRig r) => Def(null,
        Stage("s1", Users("mgrA")), Stage("s2", Users("fin1"), when: Cond(r.Total, "gt", 100000)), Stage("s3", Users("gm")));

    private static ApprovalDefinition Plain3(PurchaseRig r) => Def(null,
        Stage("s1", Users("mgrA")), Stage("s2", Users("fin1")), Stage("s3", Users("gm")));

    private static ApprovalDefinition TwoSeats(PurchaseRig r) => Def(null,
        Stage("s1", Users("mgrA")), Stage("s2", Users("fin1", "fin2"), mode: ApprovalModes.All), Stage("s3", Users("gm")));

    // ───────── 情境：略過 ─────────

    [Fact]
    public async Task ThreeStages_MiddleSkipped_ApproveAll() => await Run(ThreeStages, async s =>
    {
        var (_, id) = await Go(s, "buyer1");
        await Approve(s, id, "mgrA");
        await Approve(s, id, "gm");
    });

    [Fact]
    public async Task AllStagesSkipped_ApprovedAtSubmit() => await Run(
        r => Def(null, Stage("s1", Users("mgrA"), when: Cond(r.Total, "gt", 999999)), Stage("s2", Users("fin1"), when: Cond(r.Total, "gt", 999999))),
        async s => await Go(s, "buyer1"));

    [Fact]
    public async Task OnEmptySkip() => await Run(
        _ => Def(null, Stage("s1", Manager(1), onEmpty: ApprovalOnEmpty.Skip), Stage("s2", Users("fin1"))),
        async s =>
        {
            var (_, id) = await Go(s, "gm");   // 總部主管自己送：第 1 層主管找不到人，依 onEmpty 略過
            await Approve(s, id, "fin1");
        });

    // ───────── 情境：退回、撤回、終止 ─────────

    [Fact]
    public async Task ReturnPrevious_ThenApprove() => await Run(Plain3, async s =>
    {
        var (_, id) = await Go(s, "buyer1");
        await Approve(s, id, "mgrA");
        await Return(s, id, "fin1");
        await Approve(s, id, "mgrA");
        await Approve(s, id, "fin1");
        await Approve(s, id, "gm");
    });

    [Fact]
    public async Task ReturnPrevious_ToAddSign_Requeued() => await Run(TwoSeats, async s =>
    {
        var (_, id) = await Go(s, "buyer1");
        await Approve(s, id, "mgrA");
        await AddSign(s, id, "fin1", "emp1");
        await AddSign(s, id, "fin2", "emp2");
        await Approve(s, id, "emp1");
        await Return(s, id, "emp2");   // 第二個加簽關卡退回上一關：emp1 重簽，emp2 的加簽排回隊伍
        await Approve(s, id, "emp1");
        await Approve(s, id, "emp2");
        await Approve(s, id, "gm");
    });

    [Fact]
    public async Task RejectToInitiator_NoRejectToState() => await Run(
        _ => Def(null, Stage("s1", Users("fin1"))),
        async s =>
        {
            var (_, id) = await Go(s, "buyer1");
            await Reject(s, id, "fin1");
        });

    [Fact]
    public async Task RejectToInitiator_RejectToState() => await Run(
        r => Def(null, Stage("s1", Users("fin1"))) with { RejectTo = r.Returned },
        async s =>
        {
            var (_, id) = await Go(s, "buyer1");
            await Reject(s, id, "fin1");
        });

    [Fact]
    public async Task Withdraw_BeforeAnyAction() => await Run(Plain3, async s =>
    {
        var (_, id) = await Go(s, "buyer1");
        As("buyer1");
        await Step(s, "withdraw buyer1", Send(new WithdrawApprovalRequest(id, Inst(id).RowVersion, null)));
    });

    [Fact]
    public async Task Cancel_ByManager() => await Run(Plain3, async s =>
    {
        var (_, id) = await Go(s, "buyer1");
        await Approve(s, id, "mgrA");
        As("admin");
        await Step(s, "cancel admin", Send(new CancelApprovalRequest(id, Inst(id).RowVersion, "供應商報價已過期，請重新詢價後再送")));
    });

    // ───────── 情境：加簽、轉交、改派、代理 ─────────

    [Fact]
    public async Task AddSign_Nested() => await Run(
        _ => Def(null, Stage("s1", Users("mgrA")), Stage("s2", Users("fin1"))),
        async s =>
        {
            var (_, id) = await Go(s, "buyer1");
            await AddSign(s, id, "mgrA", "emp2");
            await AddSign(s, id, "emp2", "emp3");   // 加簽裡再加簽
            await Approve(s, id, "emp3");
            await Approve(s, id, "fin1");
        });

    [Fact]
    public async Task AddSign_TwoSiblingsSameStage() => await Run(TwoSeats, async s =>
    {
        // 會簽的兩人各加簽一筆：兩筆加簽的 StageCode 都是 null，靠正規化編號與 AddedByUserId、AddSignUserIds 區分
        var (_, id) = await Go(s, "buyer1");
        await Approve(s, id, "mgrA");
        await AddSign(s, id, "fin1", "emp1");
        await AddSign(s, id, "fin2", "emp2");
        await Approve(s, id, "emp1");
        await Approve(s, id, "emp2");
        await Approve(s, id, "gm");
    });

    [Fact]
    public async Task Transfer_ThenApprove() => await Run(
        _ => Def(null, Stage("s1", Users("fin1")), Stage("s2", Users("gm"))),
        async s =>
        {
            var (_, id) = await Go(s, "buyer1");
            await Transfer(s, id, "fin1", "fin2");
            await Approve(s, id, "fin2");
            await Approve(s, id, "gm");
        });

    [Fact]
    public async Task Reassign_ThenApprove() => await Run(
        _ => Def(null, Stage("s1", Users("fin1")), Stage("s2", Users("gm"))),
        async s =>
        {
            var (_, id) = await Go(s, "buyer1");
            await Reassign(s, id, "fin1", "emp1");
            await Approve(s, id, "emp1");
            await Approve(s, id, "gm");
        });

    [Fact]
    public async Task Delegated_ApproveByDelegate() => await Run(
        _ => Def(null, Stage("s1", Users("fin1")), Stage("s2", Users("gm"))),
        async s =>
        {
            _clock.Now = T(4, 9);
            await SetDelegate("fin1", "emp1");
            var (_, id) = await Go(s, "buyer1");
            await Approve(s, id, "emp1");   // 代理人同意，歷程記 onBehalf
            await Approve(s, id, "gm");
        });

    [Fact]
    public async Task Delegate_NotApplied_InitiatorIsDelegate() => await Run(
        _ => Def(null, Stage("s1", Users("fin1")), Stage("s2", Users("gm"))),
        async s =>
        {
            _clock.Now = T(4, 9);
            await SetDelegate("fin1", "buyer1");   // 代理人就是發起人：不套用，歷程記 not-delegated
            var (_, id) = await Go(s, "buyer1");
            await Approve(s, id, "fin1");
            await Approve(s, id, "gm");
        });

    [Fact]
    public async Task AddSign_TwoTargets_AllMode() => await Run(
        _ => Def(null, Stage("s1", Users("mgrA")), Stage("s2", Users("fin1"))),
        async s =>
        {
            var (_, id) = await Go(s, "buyer1");
            await AddSignMode(s, id, "mgrA", ApprovalModes.All, "emp1", "emp2");   // 一次加兩人、會簽
            await Approve(s, id, "emp2");
            await Approve(s, id, "emp1");
            await Approve(s, id, "fin1");
        });

    // ───────── 情境：發起人自選 ─────────

    [Fact]
    public async Task InitiatorPick_ThenApprove() => await Run(
        _ => Def(null, Stage("pick", new ApproverSource(ApprovalSources.InitiatorPick, CandidateGroupIds: [FinanceGroup], Min: 1, Max: 2),
            mode: ApprovalModes.All), Stage("s2", Users("gm"))),
        async s =>
        {
            var (_, id) = await Go(s, "buyer1", picks: new() { ["pick"] = ["fin2", "fin1"] });   // 刻意倒序：鎖住 Picks 的值與任務建立順序
            await Approve(s, id, "fin1");
            await Approve(s, id, "fin2");
            await Approve(s, id, "gm");
        });

    [Fact]
    public async Task Reroute_NewFlowNeedsPick_NeedsResubmit() => await Run(
        r => [new Spec("k", 1, Def(Cond(r.Total, "gte", 100000),
                  Stage("pick", new ApproverSource(ApprovalSources.InitiatorPick, CandidateGroupIds: [BoardGroup], Min: 1, Max: 1)))),
              new Spec("b", 2, Def(Cond(r.Total, "gte", 10000), Stage("lead", Group(PurchaseLeadGroup))))],
        async s =>
        {
            var (po, id) = await Go(s, "buyer1", 50000);
            await AddExtra(s, po, 120000);   // 總額 170000：改配到有自選節點的流程
            await Approve(s, id, "buyer2");  // rerouted＋NeedsResubmit，沒有新實例
        });

    // ───────── 情境：重新比對 ─────────

    [Fact]
    public async Task Reroute_SkipConditionReopened() => await Run(
        r => Def(Cond(r.Total, "gte", 10000), Stage("board", Group(BoardGroup), when: Cond(r.Total, "gt", 50000)), Stage("lead", Group(PurchaseLeadGroup))),
        async s =>
        {
            var (po, id) = await Go(s, "buyer1", 40000);   // 第 1 關條件不成立而略過
            await AddExtra(s, po, 20000);                   // 總額 60000：被略過的關卡條件變成立
            await Approve(s, id, "buyer2");                 // 同意時重新比對：改走新實例
            var fresh = Pending(s);
            await Approve(s, fresh, "gm");
            await Approve(s, fresh, "buyer2");
        });

    [Fact]
    public async Task Reroute_NoFlowMatches_Fallback() => await Run(
        r => Def(Cond(r.Total, "gte", 10000), Stage("lead", Group(PurchaseLeadGroup))),
        async s =>
        {
            var (po, id) = await Go(s, "buyer1", 50000);
            await AddExtra(s, po, -45000);                  // 總額 5000：沒有流程符合
            await Approve(s, id, "buyer2");                 // 改走備援實例
            await Approve(s, Pending(s), "admin");
        });

    // ───────── 情境：會簽與備援 ─────────

    [Fact]
    public async Task AllMode_TwoSeats() => await Run(
        _ => Def(null, Stage("s1", Users("fin1", "fin2"), mode: ApprovalModes.All), Stage("s2", Users("gm"))),
        async s =>
        {
            var (_, id) = await Go(s, "buyer1");
            await Approve(s, id, "fin2");
            await Approve(s, id, "fin1");
            await Approve(s, id, "gm");
        });

    [Fact]
    public async Task FallbackApprovers_Used() => await Run(
        _ => Def(null, Stage("lead", Group(PurchaseLeadGroup))) with { FallbackApprovers = Users("gm") },
        async s =>
        {
            var (_, id) = await Go(s, "buyer2");   // 群組裡只有發起人自己：沒有可簽的人，改交備援簽核人
            await Approve(s, id, "gm");
        });

    [Fact]
    public async Task Fallback_NoFallbackApprovers_AdminUsed() => await Run(
        _ => Def(null, Stage("lead", Group(PurchaseLeadGroup))),
        async s =>
        {
            var (_, id) = await Go(s, "buyer2");   // 流程沒設備援：交給系統管理員
            await Approve(s, id, "admin");
        });

    // ───────── 情境：逾期提醒 ─────────

    [Fact]
    public async Task Overdue_ReminderNotice() => await Run(
        _ => Def(null, Stage("s1", Users("mgrA"), dueHours: 24), Stage("s2", Users("fin1"), dueHours: 48)),
        async s =>
        {
            _clock.Now = T(4, 9);
            var (_, id) = await Go(s, "buyer1");
            _clock.Now = T(5, 10);                          // 第 1 關期限 11-05 09:00 已過
            await ReadNotices("mgrA");                      // 讀通知清單時補寫逾期提醒（不跑背景排程）
            _clock.Now = T(6, 10, 1);                       // 距上次提醒超過 24 小時：再提醒一次
            await ReadNotices("mgrA");
            await Approve(s, id, "mgrA");
            _clock.Now = T(9, 12);                          // 第 2 關在 11-06 10:01 啟動、期限 11-08 10:01，已過
            await ReadNotices("fin1");
        });
}
