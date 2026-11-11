using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>送出預覽與設計器試算（設計 §5.4）：和送出同一套比對與解析，不寫入。</summary>
public class ApprovalPreviewTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private async Task<ApprovalPreviewResponse> Preview(string user, long recordId, long to)
    {
        As(user);
        var res = await Send(new PreviewRecordApprovalRequest(recordId, to));
        res.IsSuccess.Should().BeTrue(Dump(res));
        return (ApprovalPreviewResponse)res.Data!;
    }

    private long EveryoneGroup() => Db.Context.Set<PermissionGroup>().AsNoTracking().Single(g => g.Key == "everyone").Id;

    private async Task<PurchaseRig> OneFlow(Func<PurchaseRig, ApprovalDefinition> d, string noMatch = ApprovalNoMatch.Block)
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-v", r.Confirmed, 1, d(r));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed, noMatch), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module);
        return r;
    }

    [Fact]
    public async Task RecordPreview_FlowStagesAndApprovers()
    {
        var r = await Scenario3();
        As("buyer1");
        var po = await NewPo(r, price: 150000);
        var p = await Preview("buyer1", po.Id, r.Confirmed);
        p.Outcome.Should().Be(ApprovalPreviewOutcomes.Flow);
        p.FlowName!.Resolve("zh-TW").Should().Be("po-a");
        p.Stages.Select(s => (s.Code, s.Status)).Should().Equal(("mgr", PreviewStatuses.Enter), ("board", PreviewStatuses.Enter));
        p.Stages[0].Approvers.Select(u => u.UserId).Should().Equal("gm");
        p.Stages[1].Approvers.Select(u => u.UserId).Should().BeEquivalentTo(["gm", "buyer2"]);
    }

    [Fact]
    public async Task RecordPreview_HiddenConditionField_Conditional_NoReason()
    {
        var r = await OneFlow(x => Def(null, Stage("lead", Group(PurchaseLeadGroup)), Stage("board", Group(BoardGroup), when: Cond(x.Discount, "gt", 0))));
        await HideFor(r.Module, r.Discount, EveryoneGroup());
        As("buyer1");
        var po = await NewPo(r, price: 5000);
        var p = await Preview("buyer1", po.Id, r.Confirmed);
        p.Stages.Select(s => (s.Code, s.Status)).Should().Equal(("lead", PreviewStatuses.Enter), ("board", PreviewStatuses.Conditional));
        p.Stages[1].Approvers.Should().BeEmpty();
        // 回應不帶條件內容：序列化後找不到被隱藏欄位的鍵或 id
        var json = JsonSerializer.Serialize(p);
        json.Should().NotContain($"f_{r.Discount}").And.NotContain("\"when\"", "回應不能帶條件");
    }

    [Fact]
    public async Task RecordPreview_SkippedStageNotListed()
    {
        // 對發起人看得到的條件不成立：記錄預覽不列這一關，也不說為什麼（skip 只出現在設計器試算）
        var r = await BuildPurchase();
        await SeedFlow(r, "po-v", r.Confirmed, 1,
            Def(null, Stage("lead", Group(PurchaseLeadGroup)), Stage("board", Group(BoardGroup), when: Cond(r.Total, "gt", 1000000))));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module);
        As("buyer1");
        var po = await NewPo(r, price: 5000);
        var p = await Preview("buyer1", po.Id, r.Confirmed);
        p.Stages.Select(s => s.Code).Should().Equal("lead");
        p.Stages.Should().NotContain(s => s.Status == PreviewStatuses.Skip);
    }

    [Fact]
    public async Task RecordPreview_DirectAndBlocked()
    {
        var r = await Scenario3();
        As("buyer1");
        var small = await NewPo(r, price: 5000);
        var direct = await Preview("buyer1", small.Id, r.Confirmed);
        direct.Outcome.Should().Be(ApprovalPreviewOutcomes.Direct);
        direct.Stages.Should().BeEmpty();
        direct.FlowName.Should().BeNull();
    }

    [Fact]
    public async Task RecordPreview_Blocked()
    {
        var r = await BuildPurchase("pb");
        await SeedFlow(r, "pb-v", r.Confirmed, 1, Def(Cond(r.Total, "gte", 10000), Stage("lead", Group(PurchaseLeadGroup))));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module);
        As("buyer1");
        var po = await NewPo(r, price: 5000);
        var p = await Preview("buyer1", po.Id, r.Confirmed);
        p.Outcome.Should().Be(ApprovalPreviewOutcomes.Blocked);
        p.Stages.Should().BeEmpty();
    }

    [Fact]
    public async Task RecordPreview_ListsPickCandidatesWithoutInitiator()
    {
        var r = await OneFlow(_ => Def(null, Stage("pick", new ApproverSource(ApprovalSources.InitiatorPick, CandidateGroupIds: [FinanceGroup], Min: 1, Max: 1))));
        As("fin1");
        var po = await NewPo(r, price: 5000);
        var p = await Preview("fin1", po.Id, r.Confirmed);
        p.Stages.Single().Status.Should().Be(PreviewStatuses.Pick);
        var pick = p.Picks.Single();
        pick.StageCode.Should().Be("pick");
        (pick.Min, pick.Max).Should().Be((1, 1));
        pick.Candidates.Select(u => u.UserId).Should().Equal("fin2");
    }

    [Fact]
    public async Task RecordPreview_SameEligibilityAsSubmit()
    {
        var r = await Scenario3();
        As("buyer1");
        var po = await NewPo(r, price: 5000);
        As("buyer1");
        var bad = await Send(new PreviewRecordApprovalRequest(po.Id, r.Partial));   // 草稿 → 部分進貨 不是已定義的轉移
        bad.IsInvalid.Should().BeTrue(Dump(bad));
        As("outsider");
        (await Send(new PreviewRecordApprovalRequest(po.Id, r.Confirmed))).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task DesignerPreview_ShowsSkipAndFallback()
    {
        var r = await OneFlow(x => Def(null, Stage("big", Group(BoardGroup), when: Cond(x.Discount, "gt", 0)), Stage("emp", Users("emp1"))));
        await Db.Context.Set<AppUser>().Where(u => u.UserId == "emp1").ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        As("admin");
        var res = await Send(new PreviewApprovalFlowRequest("po-v", null, V((r.Discount, 0), (r.Total, 5000)), "buyer1"));
        res.IsSuccess.Should().BeTrue(Dump(res));
        var p = (ApprovalPreviewResponse)res.Data!;
        p.Outcome.Should().Be(ApprovalPreviewOutcomes.Flow);
        p.Stages.Select(s => (s.Code, s.Status)).Should().Equal(("big", PreviewStatuses.Skip), ("emp", PreviewStatuses.Enter));
        p.Stages[1].UsesFallback.Should().BeTrue();
        p.Stages[1].Approvers.Select(u => u.UserId).Should().Equal("admin");

        // 以記錄值為底、Values 覆蓋
        As("buyer1");
        var po = await NewPo(r, price: 5000);
        As("admin");
        var withRecord = (ApprovalPreviewResponse)(await Send(new PreviewApprovalFlowRequest("po-v", po.Id, V((r.Discount, 10)), "buyer1"))).Data!;
        withRecord.Stages[0].Status.Should().Be(PreviewStatuses.Enter);
    }

    [Fact]
    public async Task DesignerPreview_RequiresDesigner()
    {
        await OneFlow(_ => Def(null, Stage("lead", Group(PurchaseLeadGroup))));
        As("buyer1");
        (await Send(new PreviewApprovalFlowRequest("po-v", null, null, "buyer2"))).IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task Preview_WritesNothing()
    {
        var r = await Scenario3();
        As("buyer1");
        var po = await NewPo(r, price: 150000);
        var rowVersion = Rec(po.Id).Row.RowVersion;
        await Preview("buyer1", po.Id, r.Confirmed);
        As("admin");
        (await Send(new PreviewApprovalFlowRequest("po-a", po.Id, null, "buyer1"))).IsSuccess.Should().BeTrue();
        Db.Context.Set<ApprovalInstance>().AsNoTracking().Count().Should().Be(0);
        Db.Context.Set<ApprovalAction>().AsNoTracking().Count().Should().Be(0);
        Db.Context.Set<Notification>().AsNoTracking().Count().Should().Be(0);
        Rec(po.Id).Row.RowVersion.Should().Be(rowVersion);
    }
}
