using System.Text;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.ExportService.Queries.ExportRecords;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetPagedRecords;
using DynamicModules.Application.Dynamic.SchemaService.Data;
using DynamicModules.Application.Dynamic.SchemaService.Queries.GetModuleSchema;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>
/// 清單頁「簽核中」系統篩選（設計 §13.4.1，A4C-1 到 A4C-11）：以實例表子查詢，兩種資料庫同一份 SQL。
/// 單據：P1 簽核中、P2 草稿從未送、P3 送簽後核准、P4 送簽後撤回（均為 buyer1 建立）；P5 簽核中、P6 草稿（buyer2 建立）。
/// 單據總額 50000 走流程 b（purchase-lead 群組＝buyer2 同意）。
/// </summary>
[Trait("Category", "Postgres")]
public class ApprovalListFilterTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private sealed record Rig(PurchaseRig R, long P1, long P2, long P3, long P4, long P5, long P6);

    private async Task<long> Submitted(PurchaseRig r, string user, string title)
    {
        As(user);
        var po = await NewPo(r, price: 50000, title: title);
        await MustSubmit(po.Id, r.Confirmed);
        return po.Id;
    }

    private async Task<long> Draft(PurchaseRig r, string user, string title, decimal price = 1000)
    {
        As(user);
        return (await NewPo(r, price: price, title: title)).Id;
    }

    private async Task<Rig> Arrange()
    {
        var r = await Scenario3();
        var p1 = await Submitted(r, "buyer1", "單-1 簽核中");
        var p2 = await Draft(r, "buyer1", "單-2 草稿");
        var p3 = await Submitted(r, "buyer1", "單-3 已核准");
        var p4 = await Submitted(r, "buyer1", "單-4 已撤回");
        var p5 = await Submitted(r, "buyer2", "單-5 簽核中");
        var p6 = await Draft(r, "buyer2", "單-6 草稿");

        var i3 = Db.Context.Set<ApprovalInstance>().AsNoTracking().Single(i => i.RecordId == p3).Id;
        As("buyer2");
        (await Send(new ApproveTaskRequest(
            TasksOf(i3).Single(t => t.AssigneeUserId == "buyer2" && t.Status == ApprovalTaskStatus.Open).Id, Inst(i3).RowVersion, null, null)))
            .IsSuccess.Should().BeTrue();
        Inst(i3).Status.Should().Be(ApprovalInstanceStatus.Approved);

        var i4 = Db.Context.Set<ApprovalInstance>().AsNoTracking().Single(i => i.RecordId == p4).Id;
        As("buyer1");
        (await Send(new WithdrawApprovalRequest(i4, Inst(i4).RowVersion, null))).IsSuccess.Should().BeTrue();
        Inst(i4).Status.Should().Be(ApprovalInstanceStatus.Withdrawn);
        return new Rig(r, p1, p2, p3, p4, p5, p6);
    }

    private async Task<PagedResponse<DynamicRecordResponse>> List(
        string user, string? approval, string? q = null, int page = 1, int size = 50, string[]? sortBy = null, string[]? sortOrder = null)
    {
        As(user);
        var res = await Send(new GetPagedRecordsRequest("po", page, size, null, sortBy ?? [], sortOrder ?? [], q, approval));
        res.IsSuccess.Should().BeTrue(Dump(res));
        return (PagedResponse<DynamicRecordResponse>)res.Data!;
    }

    private static long[] Ids(PagedResponse<DynamicRecordResponse> p) => p.Items.Select(i => i.Id).ToArray();

    [Fact]
    public async Task Pending_OnlyRecordsWithOpenInstance()
    {
        var a = await Arrange();
        var page = await List("fin1", "pending");
        Ids(page).Should().BeEquivalentTo([a.P1, a.P5]);
        page.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task None_RecordsWithoutOpenInstance_IncludingApprovedAndWithdrawn()
    {
        var a = await Arrange();
        var page = await List("fin1", "none");
        Ids(page).Should().BeEquivalentTo([a.P2, a.P3, a.P4, a.P6]);
        page.TotalCount.Should().Be(4);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task NoFilter_AllRecords(string? approval)
    {
        var a = await Arrange();
        var page = await List("fin1", approval);
        Ids(page).Should().BeEquivalentTo([a.P1, a.P2, a.P3, a.P4, a.P5, a.P6]);
        page.TotalCount.Should().Be(6);
    }

    [Fact]
    public async Task None_WithKeywordAndPaging_TotalCountMatches()
    {
        var a = await Arrange();
        var first = await List("fin1", "none", q: "單", page: 1, size: 3);
        var second = await List("fin1", "none", q: "單", page: 2, size: 3);
        first.TotalCount.Should().Be(4);
        second.TotalCount.Should().Be(4);
        first.Items.Should().HaveCount(3);
        second.Items.Should().HaveCount(1);
        Ids(first).Concat(Ids(second)).Should().BeEquivalentTo([a.P2, a.P3, a.P4, a.P6]).And.NotContain([a.P1, a.P5]);

        (await List("fin1", "pending", q: "草稿")).TotalCount.Should().Be(0);   // 關鍵字與簽核條件是交集
        Ids(await List("fin1", "pending", q: "簽核中")).Should().BeEquivalentTo([a.P1, a.P5]);

        // 沒有排序時依 Id 遞減，篩選不改變排序
        Ids(await List("fin1", "pending")).Should().Equal(a.P5, a.P1);
    }

    [Fact]
    public async Task OwnScope_FilterOnlyNarrows_NeverWidensRead()
    {
        var a = await Arrange();
        await SetEveryone(a.R.Module, DataScope.Own, export: true);

        var pending = await List("buyer2", "pending");
        Ids(pending).Should().Equal(a.P5);   // P1 在簽核中，但不是 buyer2 的，不洩漏存在性
        pending.TotalCount.Should().Be(1);

        var none = await List("buyer2", "none");
        Ids(none).Should().Equal(a.P6);
        none.TotalCount.Should().Be(1);

        (await List("emp1", "pending")).TotalCount.Should().Be(0);   // 沒有任何自己的單
    }

    [Fact]
    public async Task GroupScope_FilterNarrowsWithinGroupMates()
    {
        var a = await Arrange();
        await SetEveryone(a.R.Module, DataScope.Group, export: true);
        // buyer1 離開 everyone：buyer2 的群組同儕只剩其他人，P1（buyer1 的簽核中單據）不在範圍內
        var everyone = Db.Context.Set<PermissionGroup>().AsNoTracking().Single(g => g.Key == "everyone").Id;
        await Db.Context.Set<UserGroupMapping>().Where(m => m.GroupId == everyone && m.UserId == "buyer1").ExecuteDeleteAsync();

        var pending = await List("buyer2", "pending");
        Ids(pending).Should().Equal(a.P5);
        pending.TotalCount.Should().Be(1);
        Ids(await List("buyer2", "none")).Should().BeEquivalentTo([a.P6]);
    }

    [Fact]
    public async Task OwnScope_ExportCarriesFilterWithinScope()
    {
        var a = await Arrange();
        await SetEveryone(a.R.Module, DataScope.Own, export: true);
        var pending = await ExportTitles("buyer2", "pending");
        pending.Should().ContainSingle().Which.Should().Contain("單-5");
        var none = await ExportTitles("buyer2", "none");
        none.Should().ContainSingle().Which.Should().Contain("單-6");
    }

    [Theory]
    [InlineData(ApprovalInstanceStatus.Rejected)]
    [InlineData(ApprovalInstanceStatus.Cancelled)]
    [InlineData(ApprovalInstanceStatus.Rerouted)]
    public async Task EndedInstance_CountsAsNotInApproval(string ended)
    {
        var a = await Arrange();
        await Db.Context.Set<ApprovalInstance>().Where(i => i.RecordId == a.P1).ExecuteUpdateAsync(u => u.SetProperty(i => i.Status, ended));
        Ids(await List("fin1", "pending")).Should().Equal(a.P5);
        Ids(await List("fin1", "none")).Should().Contain(a.P1);
    }

    [Fact]
    public async Task SoftDeletedRecord_NeverListedByEitherValue()
    {
        var a = await Arrange();
        await Db.Context.Set<Record>().Where(r => r.Id == a.P5 || r.Id == a.P6).ExecuteUpdateAsync(u => u.SetProperty(r => r.DeleteFlag, true));
        Ids(await List("fin1", "pending")).Should().Equal(a.P1);
        Ids(await List("fin1", "none")).Should().NotContain([a.P5, a.P6]);
        (await List("fin1", "none")).TotalCount.Should().Be(3);
    }

    [Fact]
    public async Task Pending_FollowsLifecycle_SubmitAndFinish()
    {
        var a = await Arrange();
        var p7 = await Draft(a.R, "buyer1", "單-7 草稿", price: 50000);   // 總額夠大，送簽會建實例
        Ids(await List("fin1", "pending")).Should().BeEquivalentTo([a.P1, a.P5]);
        As("buyer1");
        await MustSubmit(p7, a.R.Confirmed);   // 草稿送簽後即時出現
        Ids(await List("fin1", "pending")).Should().BeEquivalentTo([a.P1, p7, a.P5]);

        var i1 = Db.Context.Set<ApprovalInstance>().AsNoTracking().Single(i => i.RecordId == a.P1).Id;
        As("buyer2");
        (await Send(new ApproveTaskRequest(
            TasksOf(i1).Single(t => t.AssigneeUserId == "buyer2" && t.Status == ApprovalTaskStatus.Open).Id, Inst(i1).RowVersion, null, null)))
            .IsSuccess.Should().BeTrue();   // 核准結束後不再是簽核中
        Ids(await List("fin1", "pending")).Should().BeEquivalentTo([p7, a.P5]);
        Ids(await List("fin1", "none")).Should().Contain(a.P1);
    }

    [Fact]
    public async Task InstanceOfAnotherModule_DoesNotCount()
    {
        var a = await Arrange();
        As("admin");
        var other = await BuildPurchase("pq");
        await GrantEveryone(other.Module);
        await SeedFlow(other, "pq-a", other.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup))));
        (await SaveWorkflow(other, [Needs(other.Draft, other.Confirmed), Plain(other.Confirmed, other.Partial), Plain(other.Partial, other.Received),
            Plain(other.Confirmed, other.Cancelled)])).IsSuccess.Should().BeTrue();
        As("buyer1");
        var po = await NewPo(other);
        await MustSubmit(po.Id, other.Confirmed);

        As("fin1");
        var res = await Send(new GetPagedRecordsRequest("pq", 1, 50, null, [], [], null, "pending"));
        ((PagedResponse<DynamicRecordResponse>)res.Data!).TotalCount.Should().Be(1);
        Ids(await List("fin1", "pending")).Should().BeEquivalentTo([a.P1, a.P5]);   // 另一個模組的實例不會讓 po 多出列
    }

    [Theory]
    [InlineData("foo")]
    [InlineData("Pending")]
    [InlineData("pending,none")]
    public async Task UnknownValue_Is400WithApprovalKey(string approval)
    {
        await Arrange();
        As("fin1");
        var res = await Send(new GetPagedRecordsRequest("po", 1, 50, null, [], [], null, approval));
        res.IsSuccess.Should().BeFalse();
        res.Errors.Should().ContainKey("approval");
    }

    private async Task<string[]> ExportTitles(string user, string? approval)
    {
        As(user);
        var res = await Send(new ExportRecordsRequest("po", "csv", null, [], [], null, "zh-TW", approval));
        res.IsSuccess.Should().BeTrue(Dump(res));
        var file = (ExportFile)res.Data!;
        return Encoding.UTF8.GetString(file.Content, 3, file.Content.Length - 3).TrimEnd('\r', '\n').Split("\r\n").Skip(1).ToArray();
    }

    [Fact]
    public async Task Export_CarriesTheApprovalFilter()
    {
        var a = await Arrange();
        await SetEveryone(a.R.Module, DataScope.All, export: true);

        var pending = await ExportTitles("fin1", "pending");
        pending.Should().HaveCount(2);
        pending.Should().OnlyContain(l => l.Contains("簽核中"));
        (await ExportTitles("fin1", "none")).Should().HaveCount(4).And.NotContain(l => l.Contains("簽核中"));
        (await ExportTitles("fin1", null)).Should().HaveCount(6);

        As("fin1");
        var bad = await Send(new ExportRecordsRequest("po", "csv", null, [], [], null, "zh-TW", "foo"));
        bad.Errors.Should().ContainKey("approval");
    }

    [Fact]
    public async Task Schema_ApprovalEnabled_FollowsApprovalTransitions_NotUserPermission()
    {
        As("admin");
        var plain = await BuildPurchase("pq");
        await GrantEveryone(plain.Module);
        As("fin1");
        Schema(await Send(new GetModuleSchemaRequest(plain.Module))).ApprovalEnabled.Should().BeFalse();

        As("admin");
        var r = await Scenario3();
        // fin1 不在任何轉移的群組名單裡（名單為空＝所有人都可），改成只有 board 能送簽，fin1 看不到那條轉移，旗標仍要是 true
        (await SaveWorkflow(r, [
            new WorkflowTransition(r.Draft, r.Confirmed, [BoardGroup], new TransitionApproval(true, ApprovalNoMatch.Direct)),
            Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received), Needs(r.Confirmed, r.Cancelled)
        ])).IsSuccess.Should().BeTrue();
        As("fin1");
        var schema = Schema(await Send(new GetModuleSchemaRequest(r.Module)));
        schema.ApprovalEnabled.Should().BeTrue();
    }

    private static ModuleSchemaResponse Schema(ServiceResult res) => (ModuleSchemaResponse)res.Data!;

    /// <summary>把 everyone 群組的資料範圍與匯出權改成指定值（其他權限同 <see cref="ApprovalTestBase.GrantEveryone"/>）。</summary>
    private async Task SetEveryone(string module, DataScope scope, bool export)
    {
        var everyone = (await Db.Context.Set<PermissionGroup>().AsNoTracking().SingleAsync(g => g.Key == "everyone")).Id;
        As("admin");
        var res = await Send(new SaveModulePermissionsRequest(module, new ModulePermissionsDto(
            [new GroupPermissionDto(everyone, new PrivilegesDto(true, true, true, true, export, true, false), scope)], [], [], [])));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }
}
