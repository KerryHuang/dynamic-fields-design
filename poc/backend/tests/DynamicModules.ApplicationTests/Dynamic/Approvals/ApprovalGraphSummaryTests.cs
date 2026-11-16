using System.Text.Json;
using System.Text.Json.Serialization;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using static DynamicModules.ApplicationTests.Dynamic.Approvals.ApprovalGraphTestKit;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>
/// 並行摘要文案的資料（計畫 fg4 Task 1；U1、U9、U10、J11、M2）：兩個以上不同節點同時進行時，記錄摘要與簽核中心回進行中的節點清單，
/// 而且只給看得到整張圖的讀者。簽核人：a＝fin1、b＝buyer2、z＝fin2、c＝gm；發起人 buyer1。
/// </summary>
public class ApprovalGraphSummaryTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // ───────── 圖與情境 ─────────

    /// <summary>start → fork f → 分支 a（fin1）、b（buyer2）→ join j → c（gm）→ end。</summary>
    private static ApprovalDefinition ForkJoin(string joinMode = ApprovalModes.All) => G(
        [Start(), Fork("f"), Approval("a", Users("fin1")), Approval("b", Users("buyer2")), Join("j", "f", joinMode), Approval("c", Users("gm")), End()],
        [E("e0", "start", "f"), E("fa", "f", "a"), E("fb", "f", "b"), E("aj", "a", "j"), E("bj", "b", "j"), E("jc", "j", "c"), E("ce", "c", "end")]);

    /// <summary>三條分支 a（fin1）、b（buyer2）、z（fin2）。</summary>
    private static ApprovalDefinition ThreeBranches() => G(
        [Start(), Fork("f"), Approval("a", Users("fin1")), Approval("b", Users("buyer2")), Approval("z", Users("fin2")), Join("j", "f"), End()],
        [E("e0", "start", "f"), E("fa", "f", "a"), E("fb", "f", "b"), E("fz", "f", "z"), E("aj", "a", "j"), E("bj", "b", "j"), E("zj", "z", "j"), E("je", "j", "end")]);

    /// <summary>兩條分支各有一個叫「主管」的節點（代號不同）。</summary>
    private static ApprovalDefinition SameNames()
    {
        var name = new I18nText([KeyValuePair.Create("zh-TW", "主管")]);
        return G(
            [Start(), Fork("f"), Approval("m1", Users("fin1")) with { Name = name }, Approval("m2", Users("buyer2")) with { Name = name }, Join("j", "f"), End()],
            [E("e0", "start", "f"), E("fa", "f", "m1"), E("fb", "f", "m2"), E("aj", "m1", "j"), E("bj", "m2", "j"), E("je", "j", "end")]);
    }

    private async Task<(PurchaseRig R, long Id)> Boot(ApprovalDefinition graph)
    {
        var r = await BuildPurchase();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, graph);
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module, r.Extra);
        As("buyer1");
        var po = await NewPo(r, price: 5000);
        return (r, (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value);
    }

    private ApprovalTask Open(long id, string user) => TasksOf(id).Single(t => t.AssigneeUserId == user && t.Status == ApprovalTaskStatus.Open);

    private async Task MustApprove(long id, string user)
    {
        As(user);
        var res = await Send(new ApproveTaskRequest(Open(id, user).Id, Inst(id).RowVersion, null, null));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    private async Task MustAddSign(long id, string user, string to)
    {
        As(user);
        var res = await Send(new AddSignTaskRequest(Open(id, user).Id, Inst(id).RowVersion, null, null, [to], ApprovalModes.Any));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    private async Task<RecordApprovalSummary> SummaryAs(long id, string user)
    {
        As(user);
        var res = await Send(new GetRecordRequest(Inst(id).RecordId));
        res.IsSuccess.Should().BeTrue(Dump(res));
        return ((DynamicRecordResponse)res.Data!).Approval!;
    }

    private async Task<ApprovalInboxItem> InboxItem(string user, string box)
    {
        As(user);
        var res = await Send(new GetApprovalTasksRequest(box, null, null, null, 1, 50));
        res.IsSuccess.Should().BeTrue(Dump(res));
        return ((DynamicModules.Application.Common.PagedResponse<ApprovalInboxItem>)res.Data!).Items.Single();
    }

    private static string[] Names(IReadOnlyList<ApprovalActiveStage>? stages) => stages!.Select(s => s.Name.Resolve("zh-TW")!).ToArray();

    /// <summary>收回全員的模組權限：之後只有持任務的簽核人靠臨時讀取權打開單據（M2）。</summary>
    private async Task RevokeRead(PurchaseRig r)
    {
        As("admin");
        await RevokeAll(r.Module);
    }

    // ───────── 記錄摘要 ─────────

    [Fact]
    public async Task RecordSummary_Graph_TwoActive_ListsBySeq()
    {
        var (_, id) = await Boot(ForkJoin());

        var summary = await SummaryAs(id, "buyer1");

        Names(summary.ActiveStages).Should().Equal("a", "b");
        summary.ActiveStages!.Should().OnlyContain(s => !s.AddSign);
        summary.StageName!.Resolve("zh-TW").Should().Be("a");   // 既有的單一欄位照舊
        summary.StageSeq.Should().Be(1);
    }

    [Fact]
    public async Task RecordSummary_Graph_ThreeActive_ListsAllThree()
    {
        var (_, id) = await Boot(ThreeBranches());

        Names((await SummaryAs(id, "buyer1")).ActiveStages).Should().Equal("a", "b", "z");
    }

    [Fact]
    public async Task RecordSummary_Graph_OneActiveOtherWaitingAtJoin_KeepsSeqCountingApprovalNodes()
    {
        // 一條分支已到匯合等另一條：只有一個進行中，維持 fg2g2 的單一文案，序號只數簽核節點
        var (_, id) = await Boot(ForkJoin());
        await MustApprove(id, "fin1");

        var summary = await SummaryAs(id, "buyer1");

        summary.ActiveStages.Should().BeNull();
        summary.StageName!.Resolve("zh-TW").Should().Be("b");
        summary.StageSeq.Should().Be(2);
    }

    [Fact]
    public async Task RecordSummary_Graph_AddSignsOnTwoBranches_ListedWithHostName()
    {
        var (_, id) = await Boot(ForkJoin());
        await MustAddSign(id, "fin1", "emp1");
        await MustAddSign(id, "buyer2", "emp2");

        var stages = (await SummaryAs(id, "buyer1")).ActiveStages!;

        Names(stages).Should().Equal("a", "b");
        stages.Should().OnlyContain(s => s.AddSign);
    }

    [Fact]
    public async Task RecordSummary_Graph_SameNameDifferentNodes_BothListed()
    {
        var (_, id) = await Boot(SameNames());

        Names((await SummaryAs(id, "buyer1")).ActiveStages).Should().Equal("主管", "主管");
    }

    [Fact]
    public async Task RecordSummary_Graph_GrantedNoReadActor_NoActiveStages()
    {
        // M2：fin1 沒有模組讀取權，只靠任務的臨時讀取權打開單據；圖上對他遮蔽，標頭也不能列出另一條分支
        var (r, id) = await Boot(ForkJoin());
        await RevokeRead(r);

        var summary = await SummaryAs(id, "fin1");

        summary.ActiveStages.Should().BeNull();
        summary.StageName!.Resolve("zh-TW").Should().Be("a");
        JsonSerializer.Serialize(summary, Web).Should().NotContain("activeStages");
    }

    [Fact]
    public async Task RecordSummary_Graph_Initiator_HasActiveStages()
    {
        // 發起人有讀取權：看得到整張圖（發起人沒有讀取權時進不了這個端點，只能走簽核歷程）
        var (_, id) = await Boot(ForkJoin());

        Names((await SummaryAs(id, "buyer1")).ActiveStages).Should().Equal("a", "b");
    }

    [Fact]
    public async Task RecordSummary_Graph_Manager_HasActiveStages()
    {
        // 簽核管理員（Approve 軸，資料範圍取自 Read 範圍，所以一定同時有 Read）：看得到整張圖；fin1 同時持有任務
        var (r, id) = await Boot(ForkJoin());
        As("admin");
        var group = new PermissionGroup { Key = "reader-approver", NameI18n = Text("reader-approver") };
        Db.Context.Add(group);
        await Db.Context.SaveChangesAsync();
        Db.Context.Add(new UserGroupMapping { GroupId = group.Id, UserId = "fin1" });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        // 全員的群組權限整批換掉：只剩 fin1 所在群組的 Read 加 Approve
        var save = await Send(new SaveModulePermissionsRequest(r.Module, new ModulePermissionsDto(
            [new GroupPermissionDto(group.Id, new PrivilegesDto(true, false, false, false, false, false, true), DataScope.All)], [], [], [])));
        save.IsSuccess.Should().BeTrue(Dump(save));

        var summary = await SummaryAs(id, "fin1");

        Names(summary.ActiveStages).Should().Equal("a", "b");
    }

    [Fact]
    public async Task RecordSummary_Sequence_JsonHasNoActiveStages()
    {
        // sequence 的摘要 JSON 不變：序列化（同 WebAPI 的選項，null 不寫出）後不含 activeStages
        var r = await Scenario3();
        As("buyer1");
        var po = await NewPo(r, price: 150000);
        var id = (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;

        var summary = await SummaryAs(id, "buyer1");

        summary.ActiveStages.Should().BeNull();
        JsonSerializer.Serialize(summary, Web).Should().Contain("\"stageName\"").And.NotContain("activeStages");
    }

    // ───────── 簽核中心 ─────────

    [Fact]
    public async Task Inbox_Submitted_Graph_Parallel_ListsActiveStages()
    {
        await Boot(ForkJoin());

        var item = await InboxItem("buyer1", ApprovalInboxBoxes.Submitted);

        Names(item.ActiveStages).Should().Equal("a", "b");
        item.StageName!.Resolve("zh-TW").Should().Be("a");
    }

    [Fact]
    public async Task Inbox_All_Graph_Parallel_ListsActiveStages()
    {
        await Boot(ForkJoin());

        Names((await InboxItem("admin", ApprovalInboxBoxes.All)).ActiveStages).Should().Equal("a", "b");
    }

    [Fact]
    public async Task Inbox_Done_ReaderWithRead_ListsActiveStages()
    {
        // 三條分支，fin1 同意後剩 b、z 兩條進行中；fin1 有模組讀取權 → 已處理列看得到並行名稱
        var (_, id) = await Boot(ThreeBranches());
        await MustApprove(id, "fin1");

        Names((await InboxItem("fin1", ApprovalInboxBoxes.Done)).ActiveStages).Should().Equal("b", "z");
    }

    [Fact]
    public async Task Inbox_Done_NoReadApprover_NoActiveStages()
    {
        // M2：fin1 沒有讀取權，已處理列維持單一名稱，與記錄摘要一致
        var (r, id) = await Boot(ThreeBranches());
        await RevokeRead(r);
        await MustApprove(id, "fin1");

        var item = await InboxItem("fin1", ApprovalInboxBoxes.Done);

        item.ActiveStages.Should().BeNull();
        item.StageName!.Resolve("zh-TW").Should().Be("b");
        JsonSerializer.Serialize(item, Web).Should().NotContain("activeStages");
    }

    [Fact]
    public async Task Inbox_Todo_Graph_Parallel_ShowsOwnNodeOnly_NoActiveStages()
    {
        // U26 不變：待簽只顯示自己的節點
        await Boot(ForkJoin());

        var item = await InboxItem("buyer2", ApprovalInboxBoxes.Todo);

        item.ActiveStages.Should().BeNull();
        item.StageName!.Resolve("zh-TW").Should().Be("b");
    }
}
