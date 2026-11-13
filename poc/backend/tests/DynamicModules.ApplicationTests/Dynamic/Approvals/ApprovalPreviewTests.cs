using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
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
    public async Task RecordPreview_RollupOfHiddenDetailField_Conditional()
    {
        // 明細小計對發起人隱藏 → 明細合計（彙總它）與總額（引用明細合計的公式）也隱藏；條件引用總額要標 conditional
        var r = await OneFlow(x => Def(null, Stage("lead", Group(PurchaseLeadGroup)), Stage("board", Group(BoardGroup), when: Cond(x.Total, "gt", 100000))));
        await HideDetailFor(r.Line, r.LineSubtotal, EveryoneGroup());
        As("buyer1");
        var po = await NewPo(r, price: 150000);
        var p = await Preview("buyer1", po.Id, r.Confirmed);
        p.Stages.Select(s => (s.Code, s.Status)).Should().Equal(("lead", PreviewStatuses.Enter), ("board", PreviewStatuses.Conditional));
        p.Stages[1].Approvers.Should().BeEmpty();
    }

    [Fact]
    public async Task RecordPreview_UserFieldSourceHidden_Conditional_NoApprovers()
    {
        // 關卡來源是使用者欄位、該欄位對發起人隱藏 → 不列簽核人（比照 conditional）
        var r = await OneFlow(x => Def(null, Stage("owner", new ApproverSource(ApprovalSources.UserField, FieldId: x.Owner))));
        As("buyer1");
        var po = await NewPo(r, price: 5000, extra: V((r.Owner, "gm")));
        As("admin");
        await HideFor(r.Module, r.Owner, EveryoneGroup());
        var p = await Preview("buyer1", po.Id, r.Confirmed);
        p.Stages.Single().Status.Should().Be(PreviewStatuses.Conditional);
        p.Stages.Single().Approvers.Should().BeEmpty();
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
    public async Task RecordPreview_PickStageWithFalseCondition_StillRequiresPick_NoConditionLeak()
    {
        // 送出驗證對所有自選關卡都要求人選：條件不成立的自選關卡不列在 stages，但 picks 仍要列出（與 PickErrors 一致）
        var r = await OneFlow(x => Def(null, Stage("lead", Group(PurchaseLeadGroup)),
            Stage("pick", new ApproverSource(ApprovalSources.InitiatorPick, CandidateGroupIds: [FinanceGroup], Min: 1, Max: 1), when: Cond(x.Total, "gt", 100000))));
        As("fin1");
        var po = await NewPo(r, price: 5000);
        var p = await Preview("fin1", po.Id, r.Confirmed);
        p.Stages.Select(s => s.Code).Should().Equal("lead");
        p.Picks.Single().StageCode.Should().Be("pick");
        p.Picks.Single().Candidates.Select(u => u.UserId).Should().Equal("fin2");
        JsonSerializer.Serialize(p).Should().NotContain("\"when\"");
    }

    [Fact]
    public async Task RecordPreview_PickStageWithHiddenConditionField_ListsPick_StageConditional()
    {
        var r = await OneFlow(x => Def(null, Stage("lead", Group(PurchaseLeadGroup)),
            Stage("pick", new ApproverSource(ApprovalSources.InitiatorPick, CandidateGroupIds: [FinanceGroup], Min: 1, Max: 1), when: Cond(x.Discount, "gt", 0))));
        await HideFor(r.Module, r.Discount, EveryoneGroup());
        As("fin1");
        var po = await NewPo(r, price: 5000);
        var p = await Preview("fin1", po.Id, r.Confirmed);
        p.Stages.Select(s => (s.Code, s.Status)).Should().Equal(("lead", PreviewStatuses.Enter), ("pick", PreviewStatuses.Conditional));
        p.Picks.Single().StageCode.Should().Be("pick");
        JsonSerializer.Serialize(p).Should().NotContain($"f_{r.Discount}");
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
    public async Task DesignerPreview_RecordWithoutRead_404()
    {
        // 只有設計器 Read、對模組沒有 Read：帶 recordId 不能拿來探查記錄值；不帶照常試算
        var r = await OneFlow(_ => Def(null, Stage("lead", Group(PurchaseLeadGroup))));
        As("buyer1");
        var po = await NewPo(r, price: 5000);
        Db.Context.Add(new UserProgramPrivilege { UserId = "fin1", ProgramCode = "DYN00000", CanRead = true });
        await Db.Context.SaveChangesAsync();
        await Db.Context.Set<UserGroupMapping>().Where(m => m.UserId == "fin1" && m.GroupId == EveryoneGroup()).ExecuteDeleteAsync();
        Db.Context.ChangeTracker.Clear();
        As("fin1");
        (await Send(new PreviewApprovalFlowRequest("po-v", po.Id, null, "buyer1"))).IsNotFound.Should().BeTrue();
        (await Send(new PreviewApprovalFlowRequest("po-v", null, null, "buyer1"))).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task DesignerPreview_MissingInitiator_400(string? initiator)
    {
        await OneFlow(_ => Def(null, Stage("lead", Group(PurchaseLeadGroup))));
        As("admin");
        (await Send(new PreviewApprovalFlowRequest("po-v", null, null, initiator!))).IsInvalid.Should().BeTrue();
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
    // ───────── 設計器試算編輯中的流程（草稿，不寫入） ─────────

    private static ApprovalFlowInput Draft(string key, long to, ApprovalDefinition d, int priority = 1, bool active = true) =>
        new(key, Text(key), to, priority, active, d);

    private async Task<ServiceResult> DraftPreview(PurchaseRig r, ApprovalFlowInput draft, long? recordId = null,
        Dictionary<string, JsonElement?>? values = null, string initiator = "buyer1")
    {
        As("admin");
        return await Send(new PreviewApprovalDraftRequest(r.Module, draft, recordId, values, initiator));
    }

    private async Task<ApprovalPreviewResponse> MustDraftPreview(PurchaseRig r, ApprovalFlowInput draft, long? recordId = null,
        Dictionary<string, JsonElement?>? values = null)
    {
        var res = await DraftPreview(r, draft, recordId, values);
        res.IsSuccess.Should().BeTrue(Dump(res));
        return (ApprovalPreviewResponse)res.Data!;
    }

    private (int Flows, int Versions) Counts() =>
        (Db.Context.Set<ApprovalFlow>().AsNoTracking().Count(), Db.Context.Set<ApprovalFlowVersion>().AsNoTracking().Count());

    [Fact]
    public async Task DraftPreview_NewFlow_NotSaved_ShowsDraftStages()
    {
        var r = await BuildPurchase();
        var before = Counts();

        var p = await MustDraftPreview(r, Draft("po-new", r.Confirmed, Def(null, Stage("mgr", Manager(1)), Stage("lead", Group(PurchaseLeadGroup)))));

        p.Outcome.Should().Be(ApprovalPreviewOutcomes.Flow);
        p.FlowKey.Should().Be("po-new");
        p.Stages.Select(s => s.Code).Should().Equal("mgr", "lead");
        Counts().Should().Be(before);
        Db.Context.Set<ApprovalInstance>().AsNoTracking().Count().Should().Be(0);
        Db.Context.Set<ApprovalAction>().AsNoTracking().Count().Should().Be(0);
        Db.Context.Set<Notification>().AsNoTracking().Count().Should().Be(0);
    }

    [Fact]
    public async Task DraftPreview_WithRecord_WritesNothing()
    {
        var r = await Scenario3();
        As("buyer1");
        var po = await NewPo(r, price: 150000);
        var rowVersion = Rec(po.Id).Row.RowVersion;
        var before = Counts();

        await MustDraftPreview(r, Draft("po-new", r.Confirmed, Def(null, Stage("mgr", Manager(1))), priority: 9), recordId: po.Id);

        Counts().Should().Be(before);
        Db.Context.Set<ApprovalInstance>().AsNoTracking().Count().Should().Be(0);
        Db.Context.Set<ApprovalAction>().AsNoTracking().Count().Should().Be(0);
        Db.Context.Set<Notification>().AsNoTracking().Count().Should().Be(0);
        Rec(po.Id).Row.RowVersion.Should().Be(rowVersion);
    }

    [Fact]
    public async Task DraftPreview_ReplacesSavedVersionOfSameKey()
    {
        var r = await OneFlow(x => Def(Cond(x.Total, "gte", 100000), Stage("lead", Group(PurchaseLeadGroup))));
        var values = V((r.Total, 50));
        As("admin");
        var saved = (ApprovalPreviewResponse)(await Send(new PreviewApprovalFlowRequest("po-v", null, values, "buyer1"))).Data!;
        saved.Outcome.Should().Be(ApprovalPreviewOutcomes.Blocked);

        var p = await MustDraftPreview(r, Draft("po-v", r.Confirmed, Def(Cond(r.Total, "gte", 1), Stage("lead", Group(PurchaseLeadGroup)))), values: values);

        p.Outcome.Should().Be(ApprovalPreviewOutcomes.Flow);
        p.FlowKey.Should().Be("po-v");
        // 已存版本沒被草稿改動
        var saved2 = Db.Context.Set<ApprovalFlowVersion>().AsNoTracking().Single();
        saved2.Version.Should().Be(1);
        saved2.Definition.When!.Value.GetRawText().Should().Contain("100000");
    }

    [Fact]
    public async Task DraftPreview_HigherPriorityFlowWins_FlowKeyIsOther()
    {
        var r = await OneFlow(x => Def(null, Stage("lead", Group(PurchaseLeadGroup))));

        var p = await MustDraftPreview(r, Draft("po-draft", r.Confirmed, Def(null, Stage("mgr", Manager(1))), priority: 2));

        p.FlowKey.Should().Be("po-v");
        p.Stages.Select(s => s.Code).Should().Equal("lead");
    }

    [Fact]
    public async Task DraftPreview_InactiveDraft_TreatedAsActive()
    {
        var r = await BuildPurchase();
        var p = await MustDraftPreview(r, Draft("po-off", r.Confirmed, Def(null, Stage("mgr", Manager(1))), active: false));
        (p.Outcome, p.FlowKey).Should().Be((ApprovalPreviewOutcomes.Flow, "po-off"));
    }

    [Fact]
    public async Task DraftPreview_InvalidStageCode_Returns400WithDefinitionKey()
    {
        var r = await BuildPurchase();
        var res = await DraftPreview(r, Draft("po-new", r.Confirmed, Def(null, Stage("Bad Code", Manager(1)))));
        res.IsInvalid.Should().BeTrue(Dump(res));
        res.Errors.Should().ContainKey("definition.stages[0].code");
    }

    [Fact]
    public async Task DraftPreview_TargetIsInitialState_Returns400()
    {
        var r = await BuildPurchase();
        var res = await DraftPreview(r, Draft("po-new", r.Draft, Def(null, Stage("mgr", Manager(1)))));
        res.IsInvalid.Should().BeTrue(Dump(res));
        res.Errors.Should().ContainKey("toOptionId");
    }

    [Fact]
    public async Task DraftPreview_RecordOfOtherModule_Returns404()
    {
        var r = await OneFlow(_ => Def(null, Stage("lead", Group(PurchaseLeadGroup))));
        var other = await BuildPurchase("pq");
        await GrantEveryone(other.Module);
        As("buyer1");
        var po = await NewPo(other, price: 5000);
        var res = await DraftPreview(r, Draft("po-new", r.Confirmed, Def(null, Stage("mgr", Manager(1)))), recordId: po.Id);
        res.IsNotFound.Should().BeTrue();
    }

    private async Task<long> EmptyGroup()
    {
        var g = new PermissionGroup { Key = "nobody", NameI18n = Text("nobody") };
        Db.Context.Add(g);
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        return g.Id;
    }

    [Fact]
    public async Task DesignerPreview_UnresolvableStage_ReportsNoApproverProblem()
    {
        var r = await BuildPurchase();
        var empty = await EmptyGroup();
        var def = new ApprovalDefinition(ApprovalShapes.Sequence, null, null, Group(empty),
            [Stage("ok", Group(PurchaseLeadGroup)), Stage("none", Group(empty))]);

        var p = await MustDraftPreview(r, Draft("po-new", r.Confirmed, def));

        p.Stages.Select(s => (s.Code, s.Problem)).Should().Equal(("ok", null), ("none", PreviewProblems.NoApprover));
        p.Stages[1].Approvers.Should().BeEmpty();
    }

    [Fact]
    public async Task DesignerPreview_OverStageLimit_ReportsTooManyProblem()
    {
        var r = await BuildPurchase();
        Db.Context.Add(new ModulePolicy { ModuleId = r.ModuleId, Code = PolicyCatalog.ApprovalMaxApprovers.Code, Value = "1", ModifiedUserId = "admin" });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();

        var p = await MustDraftPreview(r, Draft("po-new", r.Confirmed, Def(null, Stage("fin", Group(FinanceGroup)))));

        p.Stages.Should().ContainSingle().Which.Problem.Should().Be(PreviewProblems.TooManyApprovers);
    }

    [Fact]
    public async Task RecordPreview_NoFlowKeyNoProblem()
    {
        var r = await OneFlow(_ => Def(null, Stage("lead", Group(PurchaseLeadGroup))));
        As("buyer1");
        var po = await NewPo(r, price: 5000);
        var p = await Preview("buyer1", po.Id, r.Confirmed);
        p.FlowKey.Should().BeNull();
        p.Stages.Should().OnlyContain(s => s.Problem == null);
    }
}
