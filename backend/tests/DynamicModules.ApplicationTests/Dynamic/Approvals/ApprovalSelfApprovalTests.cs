using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>A12：發起人能否簽自己的單（approval.allowSelfApproval，模組範圍，出廠 false）。</summary>
public class ApprovalSelfApprovalTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private Task<PurchaseRig> WithFlow(ApprovalDefinition d, bool allow) => WithFlow(_ => d, allow);

    private async Task<PurchaseRig> WithFlow(Func<PurchaseRig, ApprovalDefinition> d, bool allow)
    {
        var r = await Scenario3();
        await SeedFlow(r, "po-x", r.Confirmed, 0, d(r));
        if (allow)
        {
            Db.Context.Add(new ModulePolicy
            {
                ModuleId = r.ModuleId, Code = PolicyCatalog.ApprovalAllowSelfApproval.Code, Value = "true", ModifiedUserId = "admin"
            });
            await Db.Context.SaveChangesAsync();
            Db.Context.ChangeTracker.Clear();
        }
        return r;
    }

    private async Task<ServiceResult> SubmitAs(PurchaseRig r, string user, Dictionary<string, List<string>>? picks = null,
        Dictionary<string, System.Text.Json.JsonElement?>? extra = null)
    {
        As(user);
        var po = await NewPo(r, price: 100, extra: extra);
        return await Send(new SubmitApprovalRequest(po.Id, r.Confirmed, Rec(po.Id).Row.RowVersion, picks, null));
    }

    private static long InstanceOf(ServiceResult res) => ((SubmitApprovalResponse)res.Data!).InstanceId!.Value;

    private IEnumerable<string> OpenAssignees(long id) =>
        TasksOf(id).Where(t => t.Status == ApprovalTaskStatus.Open).Select(t => t.AssigneeUserId).Order();

    [Fact]
    public async Task SelfApproval_Factory_InitiatorRemoved()
    {
        var r = await WithFlow(Def(null, Stage("fin", Group(FinanceGroup))), allow: false);
        var res = await SubmitAs(r, "fin1");
        res.IsSuccess.Should().BeTrue(Dump(res));
        OpenAssignees(InstanceOf(res)).Should().Equal("fin2");
    }

    [Fact]
    public async Task SelfApproval_Allowed_InitiatorKeptAndMustAct()
    {
        var r = await WithFlow(Def(null, Stage("fin", Group(FinanceGroup), mode: ApprovalModes.All)), allow: true);
        var res = await SubmitAs(r, "fin1");
        res.IsSuccess.Should().BeTrue(Dump(res));
        var id = InstanceOf(res);
        OpenAssignees(id).Should().Equal("fin1", "fin2");
        ActionsOf(id).Select(a => a.Action).Should().Equal(ApprovalActions.Submit);   // 沒有自動同意

        As("fin1");
        var task = TasksOf(id).Single(t => t.AssigneeUserId == "fin1");
        (await Send(new ApproveTaskRequest(task.Id, Inst(id).RowVersion, null, null))).IsSuccess.Should().BeTrue();
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);   // 會簽還差 fin2，本人按了才算一票
        ActionsOf(id).Select(a => a.Action).Should().Contain(ApprovalActions.Approve);
    }

    [Fact]
    public async Task SelfApproval_Allowed_PickIncludesInitiator_Succeeds()
    {
        var pick = new ApproverSource(ApprovalSources.InitiatorPick, CandidateGroupIds: [FinanceGroup], Min: 1, Max: 2);
        var r = await WithFlow(Def(null, Stage("p", pick)), allow: true);
        var res = await SubmitAs(r, "fin1", new() { ["p"] = ["fin1"] });
        res.IsSuccess.Should().BeTrue(Dump(res));
        OpenAssignees(InstanceOf(res)).Should().Equal("fin1");
    }

    [Fact]
    public async Task SelfApproval_Factory_PickIncludesInitiator_Returns400()
    {
        var pick = new ApproverSource(ApprovalSources.InitiatorPick, CandidateGroupIds: [FinanceGroup], Min: 1, Max: 2);
        var r = await WithFlow(Def(null, Stage("p", pick)), allow: false);
        var res = await SubmitAs(r, "fin1", new() { ["p"] = ["fin1"] });
        res.IsSuccess.Should().BeFalse();
        res.Errors.Should().ContainKey("picks.p");
    }

    private ApprovalDefinition OwnerFlow(PurchaseRig r) =>
        Def(null, Stage("o", new ApproverSource(ApprovalSources.UserField, FieldId: r.Owner, CandidateGroupIds: [FinanceGroup])));

    [Fact]
    public async Task SelfApproval_Allowed_UserFieldIsInitiator_Succeeds()
    {
        var r = await WithFlow(OwnerFlow, allow: true);
        var res = await SubmitAs(r, "fin1", extra: V((r.Owner, "fin1")));
        res.IsSuccess.Should().BeTrue(Dump(res));
        OpenAssignees(InstanceOf(res)).Should().Equal("fin1");
    }

    [Fact]
    public async Task SelfApproval_Factory_UserFieldIsInitiator_Returns400()
    {
        var r = await WithFlow(OwnerFlow, allow: false);
        var res = await SubmitAs(r, "fin1", extra: V((r.Owner, "fin1")));
        res.IsSuccess.Should().BeFalse();
        res.Errors.Should().ContainKey($"f_{r.Owner}");
    }

    [Fact]
    public async Task SelfApproval_Allowed_ManagerSourceStillGoesUp()
    {
        var r = await WithFlow(Def(null, Stage("m", Manager(1))), allow: true);
        var res = await SubmitAs(r, "lead1");   // 業務一課的主管就是 lead1 本人 → 往上找
        res.IsSuccess.Should().BeTrue(Dump(res));
        OpenAssignees(InstanceOf(res)).Should().Equal("mgrA");
    }

    [Theory]
    [InlineData(true, new[] { "fin2", "gm" })]
    [InlineData(false, new[] { "fin2" })]
    public async Task SelfApproval_FallbackList_KeepsInitiatorOnlyWhenAllowed(bool allow, string[] expected)
    {
        // gm 是最上層、沒有主管 → 這關交給備援；備援名單含發起人 gm
        var r = await WithFlow(Def(null, Stage("m", Manager(1))) with { FallbackApprovers = Users(["gm", "fin2"]) }, allow);
        var res = await SubmitAs(r, "gm");
        res.IsSuccess.Should().BeTrue(Dump(res));
        OpenAssignees(InstanceOf(res)).Should().Equal(expected);
    }

    private async Task PreviewCheck(bool allow)
    {
        var pick = new ApproverSource(ApprovalSources.InitiatorPick, CandidateGroupIds: [FinanceGroup], Min: 1, Max: 2);
        var r = await WithFlow(Def(null, Stage("fin", Group(FinanceGroup)), Stage("p", pick)), allow);
        As("fin1");
        var po = await NewPo(r, price: 100);
        var res = await Send(new PreviewRecordApprovalRequest(po.Id, r.Confirmed));
        res.IsSuccess.Should().BeTrue(Dump(res));
        var p = (ApprovalPreviewResponse)res.Data!;
        p.Stages.First(s => s.Code == "fin").Approvers.Select(u => u.UserId).Contains("fin1").Should().Be(allow);
        p.Picks.Single().Candidates.Select(u => u.UserId).Contains("fin1").Should().Be(allow);
    }

    [Fact]
    public Task Preview_Factory_OmitsInitiator() => PreviewCheck(false);

    [Fact]
    public Task Preview_SelfApprovalAllowed_ListsInitiator() => PreviewCheck(true);
}
