using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.PrintService;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

/// <summary>
/// Demo 的進銷存採購單（簽核 a5c）：草稿 → 已確認 由套件宣告成需簽核，9 張採購單一律經送簽確認；
/// 未稅小計 10,000 以上一關（ops01）、100,000 以上兩關（ops01、manager01）、不到門檻送出即確認，另有一張大額停在第二關。
/// </summary>
[Collection(DemoGeneratedCollection.Name)]
public class InventoryPoApprovalScenarioTests(DemoGeneratedFixture fx)
{
    private const string Po = "inv-po";
    private const decimal MediumFrom = 10000, LargeFrom = 100000;

    private sealed record PoRow(DynamicRecordResponse Record, string Status, decimal Total);   // Total 是未稅小計（門檻比對的欄位）

    private async Task<List<PoRow>> Pos()
    {
        fx.Generated();
        var status = await fx.Key(Po, "狀態");
        var total = await fx.Key(Po, "未稅小計");
        var labels = (await fx.Module(Po)).Blocks.SelectMany(b => b.Fields).Single(f => f.Key == status).Options
            .ToDictionary(o => o.Value, o => o.Label.Resolve("zh-TW"));
        return [.. (await fx.Records(Po)).Select(r => new PoRow(r, labels[DemoGeneratedFixture.Text(r, status)!],
            r.Values[total]!.Value.GetDecimal()))];
    }

    private async Task<List<(ApprovalInstance Instance, string FlowKey)>> Instances()
    {
        var db = fx.Host.Db.Context;
        var flows = await db.Set<ApprovalFlow>().AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.Key);
        return [.. (await db.Set<ApprovalInstance>().AsNoTracking().ToListAsync()).Select(i => (i, i.FlowId is { } id ? flows[id] : ""))];
    }

    private async Task<List<(string Stage, string User, string Status)>> TasksOf(long instanceId)
    {
        var db = fx.Host.Db.Context;
        var runs = await db.Set<ApprovalStageRun>().AsNoTracking().Where(r => r.InstanceId == instanceId).ToDictionaryAsync(r => r.Id);
        return [.. (await db.Set<ApprovalTask>().AsNoTracking().Where(t => t.InstanceId == instanceId).ToListAsync())
            .OrderBy(t => runs[t.StageRunId].Seq).Select(t => (runs[t.StageRunId].StageCode ?? "", t.AssigneeUserId, t.Status))];
    }

    [Fact]
    public async Task GraphExampleFlow_IsPublishedGraphButInactive_AndNeverUsedBySubmissions()
    {
        fx.Generated();
        var db = fx.Host.Db.Context;
        var flow = await db.Set<ApprovalFlow>().AsNoTracking().SingleAsync(f => f.Key == "inv-po-graph-example");
        flow.Active.Should().BeFalse("範例只給設計頁與試算，不影響既有送簽情境");
        flow.CurrentVersionId.Should().NotBeNull("已發布第 1 版");
        flow.NameI18n.Resolve("zh-TW").Should().Be("採購單（流程圖範例）");
        flow.NameI18n.Values.Keys.Should().BeEquivalentTo("zh-TW", "en-US", "th", "vi");
        flow.Priority.Should().Be(0, "排在套件兩條關卡格式流程前面，設計頁試算才會先命中自己");
        ApprovalFlowDraft.TokenOf(flow.DraftJson).Should().NotBeNull();
        ApprovalFlowDraft.TryParse(flow.DraftJson).Should().BeNull("發布後草稿內容已清空");

        var version = await db.Set<ApprovalFlowVersion>().AsNoTracking().SingleAsync(v => v.Id == flow.CurrentVersionId);
        version.Version.Should().Be(1);
        version.Definition.Shape.Should().Be("graph");
        version.Definition.Nodes!.Select(n => n.Kind).Should().BeEquivalentTo(["start", "condition", "fork", "approval", "approval", "join", "approval", "approval", "end"]);
        version.Definition.Nodes!.Single(n => n.Kind == "join").Mode.Should().Be("all");
        version.Definition.Edges!.Count(e => e.From == "amount").Should().Be(2, "條件節點一條有條件、一條預設");

        var others = await db.Set<ApprovalFlow>().AsNoTracking().Where(f => f.ToOptionId == flow.ToOptionId && f.Key != flow.Key).ToListAsync();
        others.Should().OnlyContain(f => f.Active, "既有兩條關卡格式流程維持啟用");
        (await Instances()).Should().NotContain(i => i.FlowKey == "inv-po-graph-example");
    }

    [Fact]
    public async Task InventoryPos_NineRecords_WithEveryOutcome()
    {
        var pos = await Pos();
        pos.Should().HaveCount(9);
        pos.GroupBy(p => p.Status).ToDictionary(g => g.Key, g => g.Count())
            .Should().Equal(new Dictionary<string, int> { ["草稿"] = 2, ["已確認"] = 4, ["已結案"] = 3 }, "草稿含一張大額簽核中的單");
        pos.Should().Contain(p => p.Total < MediumFrom, "小額一張");
        pos.Count(p => p.Total >= LargeFrom).Should().Be(2, "大額兩張");
    }

    [Fact]
    public async Task InventoryPos_ConfirmedOnlyThroughSubmit()
    {
        var pos = await Pos();
        var instances = await Instances();
        foreach (var p in pos.Where(p => p.Status is "已確認" or "已結案"))
        {
            var mine = instances.Where(i => i.Instance.RecordId == p.Record.Id).ToList();
            if (p.Total >= MediumFrom)
            {
                var only = mine.Should().ContainSingle($"總額 {p.Total} 達門檻要有一個簽核實例").Subject;
                only.Instance.Status.Should().Be(ApprovalInstanceStatus.Approved);
                only.Instance.InitiatorUserId.Should().Be("buyer01");
                only.FlowKey.Should().Be(p.Total >= LargeFrom ? "inv-po-large" : "inv-po-medium");
            }
            else mine.Should().BeEmpty($"總額 {p.Total} 不到門檻，送出即確認，沒有簽核實例");
        }
    }

    [Fact]
    public async Task LargePo_ApprovedByLevel1ThenLevel2()
    {
        var pos = await Pos();
        var large = pos.Single(p => p.Total >= LargeFrom && p.Status == "已確認");
        var instance = (await Instances()).Single(i => i.Instance.RecordId == large.Record.Id).Instance;
        (await TasksOf(instance.Id)).Should().Equal(
            [("buyer-lead", "ops01", ApprovalTaskStatus.Approved), ("", "sales01", ApprovalTaskStatus.Approved), ("gm", "manager01", ApprovalTaskStatus.Approved)],
            "第 1 關營運部主管同意並加簽業務一（加簽關卡沒有關卡代碼）、第 2 關總經理室主管，依序同意");
    }

    [Fact]
    public async Task LargePo_PendingAtSecondStage_InManager01Todo()
    {
        var pos = await Pos();
        var pending = pos.Single(p => p.Total >= LargeFrom && p.Status == "草稿");
        var instance = (await Instances()).Single(i => i.Instance.RecordId == pending.Record.Id).Instance;
        instance.Status.Should().Be(ApprovalInstanceStatus.Pending);
        (await TasksOf(instance.Id)).Should().Equal(
            [("buyer-lead", "ops01", ApprovalTaskStatus.Approved), ("gm", "manager01", ApprovalTaskStatus.Open)]);

        var inbox = await fx.As("manager01", new GetApprovalTasksRequest("todo", null, null, null, 1, 50));
        inbox.IsSuccess.Should().BeTrue(inbox.Message);
        ((PagedResponse<ApprovalInboxItem>)inbox.Data!).Items.Should().Contain(i => i.RecordId == pending.Record.Id, "manager01 的簽核中心有這一張待簽");

        // manager01 不在任何進銷存群組：靠待簽任務的臨時讀取打開單據，看得到含稅總計（金額軸一併開放）
        var read = await fx.As("manager01", new GetRecordRequest(pending.Record.Id));
        read.IsSuccess.Should().BeTrue(read.Message);
        var detail = (DynamicRecordResponse)read.Data!;
        detail.Values.GetValueOrDefault(await fx.Key(Po, "含稅總計")).Should().NotBeNull("臨時讀取連金額軸一起開放");
    }

    [Fact]
    public async Task PoSheet_PendingApprover_CanPrint_ThenRejectedOnceApproved()
    {
        var pos = await Pos();
        var instances = await Instances();
        var pending = pos.Single(p => p.Total >= LargeFrom && p.Status == "草稿" && instances.Any(i => i.Instance.RecordId == p.Record.Id && i.Instance.Status == ApprovalInstanceStatus.Pending));
        var done = pos.First(p => p.Total >= LargeFrom && p.Status == "已確認");

        // manager01 沒有任何進銷存模組權限：待簽期間靠臨時讀取列印採購單範本
        var printed = await fx.As("manager01", new PrintRecordRequest(pending.Record.Id, "inv-po-sheet", "zh-TW"));
        printed.IsSuccess.Should().BeTrue(printed.Message);
        ((PrintedDocument)printed.Data!).Html.Should().Contain("採購單").And.Contain("含稅總計");

        // 同一個人對已簽完的單據：任務已結束，臨時讀取收回
        (await fx.As("manager01", new PrintRecordRequest(done.Record.Id, "inv-po-sheet", "zh-TW"))).IsForbidden.Should().BeTrue();
    }

    private async Task<List<ApprovalAction>> ActionsOf(long instanceId) =>
        await fx.Host.Db.Context.Set<ApprovalAction>().AsNoTracking().Where(a => a.InstanceId == instanceId).OrderBy(a => a.Id).ToListAsync();

    private async Task<long> InstanceWithAction(string action)
    {
        fx.Generated();
        var ids = await fx.Host.Db.Context.Set<ApprovalAction>().AsNoTracking().Where(a => a.Action == action).Select(a => a.InstanceId).Distinct().ToListAsync();
        return ids.Should().ContainSingle($"Demo 只有一張採購單有 {action}").Subject;
    }

    [Fact]
    public async Task Demo_TransferredPo_HistoryHasTransferToManager01()
    {
        var id = await InstanceWithAction(ApprovalActions.Transfer);
        var actions = await ActionsOf(id);
        var transfer = actions.Single(a => a.Action == ApprovalActions.Transfer);
        transfer.ActorUserId.Should().Be("ops01");
        transfer.TargetUserIds.Should().Equal("manager01");
        actions.Single(a => a.Action == ApprovalActions.Approve).ActorUserId.Should().Be("manager01");
        (await Instances()).Single(i => i.Instance.Id == id).Instance.Status.Should().Be(ApprovalInstanceStatus.Approved);
    }

    [Fact]
    public async Task Demo_AddSignPo_AddSignStageBySales01()
    {
        var id = await InstanceWithAction(ApprovalActions.AddSign);
        var db = fx.Host.Db.Context;
        var run = await db.Set<ApprovalStageRun>().AsNoTracking().SingleAsync(r => r.InstanceId == id && r.AddedByUserId == "ops01");
        run.Status.Should().Be(ApprovalStageStatus.Approved);
        (await db.Set<ApprovalTask>().AsNoTracking().Where(t => t.StageRunId == run.Id).ToListAsync())
            .Should().ContainSingle(t => t.AssigneeUserId == "sales01" && t.Status == ApprovalTaskStatus.Approved);
        var gm = (await TasksOf(id)).Single(t => t.Stage == "gm");
        (gm.User, gm.Status).Should().Be(("manager01", ApprovalTaskStatus.Approved));
        (await Instances()).Single(i => i.Instance.Id == id).Instance.Status.Should().Be(ApprovalInstanceStatus.Approved);
    }

    [Fact]
    public async Task Demo_CancelledDraftPo_InstanceCancelled_RecordDraft()
    {
        var id = await InstanceWithAction(ApprovalActions.Cancel);
        var instance = (await Instances()).Single(i => i.Instance.Id == id).Instance;
        instance.Status.Should().Be(ApprovalInstanceStatus.Cancelled);
        instance.InitiatorUserId.Should().Be("buyer01");
        var cancel = (await ActionsOf(id)).Single(a => a.Action == ApprovalActions.Cancel);
        cancel.ActorUserId.Should().Be("admin");
        cancel.Comment.Should().NotBeNullOrWhiteSpace();
        (await Pos()).Single(p => p.Record.Id == instance.RecordId).Status.Should().Be("草稿");
    }

    [Fact]
    public async Task Demo_PastDelegation_TaskDelegatedToSales01_OnBehalfOfOps01()
    {
        fx.Generated();
        var db = fx.Host.Db.Context;
        var task = await db.Set<ApprovalTask>().AsNoTracking().SingleAsync(t => t.Reason == ApprovalTaskReason.Delegated);
        (task.AssigneeUserId, task.OriginalUserId, task.Status).Should().Be(("sales01", "ops01", ApprovalTaskStatus.Approved));
        (await ActionsOf(task.InstanceId)).Single(a => a.Action == ApprovalActions.Approve).OnBehalfOfUserId.Should().Be("ops01");
    }

    [Fact]
    public async Task Demo_NoActiveDelegationToday()
    {
        fx.Generated();
        var delegates = await fx.Host.Db.Context.Set<ApprovalDelegate>().AsNoTracking().ToListAsync();
        delegates.Should().NotBeEmpty();
        var today = DemoTestHost.Today;   // fixture 的營運日，不用機器時鐘
        delegates.Should().OnlyContain(d => d.EndDate < today);
        delegates.Select(d => (Active: d.StartDate <= today && today <= d.EndDate, Upcoming: today < d.StartDate))
            .Should().OnlyContain(s => !s.Active && !s.Upcoming);
    }
}
