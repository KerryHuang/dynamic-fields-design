using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>簽核上限改讀政策：存流程只擋「這次增加的」（政策設計 §3.5），出廠值等於原寫死值。</summary>
public class ApprovalPolicyLimitsTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private static ApprovalFlowInput Input(string key, long to, ApprovalDefinition d, string? name = null) =>
        new(key, Text(name ?? key), to, 1, true, d);

    private static ApprovalStageDef[] Stages(int n) => Enumerable.Range(1, n).Select(i => Stage($"s{i}", Manager(1))).ToArray();

    private async Task SetSystem(PolicyDefinition def, string value)
    {
        await Svc<ISystemSettingRepository>().SetAsync(def.SettingKey, value, default);
        await Db.Context.SaveChangesAsync();
    }

    private async Task SetOverride(PurchaseRig r, PolicyDefinition def, string value)
    {
        Db.Context.Add(new ModulePolicy { ModuleId = r.ModuleId, Code = def.Code, Value = value, ModifiedUserId = "admin" });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
    }

    private async Task AddUsers(int n)
    {
        for (var i = 1; i <= n; i++) Db.Context.Add(new AppUser { UserId = $"u{i:00}", DisplayName = $"u{i:00}" });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
    }

    private static string[] UserIds(int n) => Enumerable.Range(1, n).Select(i => $"u{i:00}").ToArray();

    private async Task<ServiceResultView> Create(PurchaseRig r, ApprovalFlowInput input) =>
        new(await Send(new CreateApprovalFlowRequest(r.Module, input)));

    private async Task<ServiceResultView> Update(PurchaseRig r, ApprovalFlowInput input) =>
        new(await Send(new UpdateApprovalFlowRequest(input.Key, input)));

    private sealed record ServiceResultView(ServiceResult R)
    {
        public bool Ok => R.IsSuccess;
        public string Text(string key) => R.Errors.TryGetValue(key, out var e) ? e[0] : "";
        public string All => Dump(R);
    }

    [Fact]
    public async Task SaveFlow_FactoryLimits_SameAsBefore()
    {
        var r = await BuildPurchase();
        var res = await Create(r, Input("f1", r.Confirmed, Def(null, Stages(11))));
        res.Ok.Should().BeFalse();
        res.Text("definition.stages").Should().Contain("10").And.NotContain("沿用原本的數字");
        (await Create(r, Input("f2", r.Confirmed, Def(null, Stages(10))))).Ok.Should().BeTrue();
        (await Update(r, Input("f2", r.Confirmed, Def(null, Stages(11))))).Text("definition.stages").Should().Contain("沿用原本的數字");
    }

    [Fact]
    public async Task SaveFlow_ModuleOverrideStages3_NewFlowWith4_Returns400()
    {
        var r = await BuildPurchase();
        await SetOverride(r, PolicyCatalog.ApprovalMaxStages, "3");
        var res = await Create(r, Input("f1", r.Confirmed, Def(null, Stages(4))));
        res.Text("definition.stages").Should().Contain("3");
        (await Create(r, Input("f2", r.Confirmed, Def(null, Stages(3))))).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task SaveFlow_StagesOverLoweredLimit_Unchanged_Allowed()
    {
        var r = await BuildPurchase();
        (await Create(r, Input("f1", r.Confirmed, Def(null, Stages(5))))).Ok.Should().BeTrue();
        await SetOverride(r, PolicyCatalog.ApprovalMaxStages, "3");
        var res = await Update(r, Input("f1", r.Confirmed, Def(null, Stages(5)), "改名"));
        res.Ok.Should().BeTrue(res.All);
        // 減少也允許
        (await Update(r, Input("f1", r.Confirmed, Def(null, Stages(4))))).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task SaveFlow_StagesOverLoweredLimit_AddOne_Returns400()
    {
        var r = await BuildPurchase();
        (await Create(r, Input("f1", r.Confirmed, Def(null, Stages(5))))).Ok.Should().BeTrue();
        await SetOverride(r, PolicyCatalog.ApprovalMaxStages, "3");
        (await Update(r, Input("f1", r.Confirmed, Def(null, Stages(6))))).Text("definition.stages").Should().Contain("3");
    }

    [Fact]
    public async Task SaveFlow_ApproversPerStage_ExistingStageUnchanged_Allowed()
    {
        var r = await BuildPurchase();
        await AddUsers(25);
        (await Create(r, Input("f1", r.Confirmed, Def(null, Stage("a", Users(UserIds(15))))))).Ok.Should().BeTrue();
        await SetOverride(r, PolicyCatalog.ApprovalMaxApprovers, "10");
        (await Update(r, Input("f1", r.Confirmed, Def(null, Stage("a", Users(UserIds(15)))), "改名"))).Ok.Should().BeTrue();
        (await Update(r, Input("f1", r.Confirmed, Def(null, Stage("a", Users(UserIds(12))))))).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task SaveFlow_ApproversPerStage_Increased_Returns400()
    {
        var r = await BuildPurchase();
        await AddUsers(25);
        (await Create(r, Input("f1", r.Confirmed, Def(null, Stage("a", Users(UserIds(15))))))).Ok.Should().BeTrue();
        await SetOverride(r, PolicyCatalog.ApprovalMaxApprovers, "10");
        (await Update(r, Input("f1", r.Confirmed, Def(null, Stage("a", Users(UserIds(16))))))).Text("definition.stages[0].approvers").Should().Contain("10");
        // 同一代碼對照：換個代碼就是新關卡，直接比上限
        (await Update(r, Input("f1", r.Confirmed, Def(null, Stage("b", Users(UserIds(15))))))).Text("definition.stages[0].approvers").Should().Contain("10");
    }

    [Fact]
    public async Task SaveFlow_FallbackUsersOverLoweredLimit_Unchanged_Allowed()
    {
        var r = await BuildPurchase();
        await AddUsers(25);
        ApprovalDefinition D(int n) => Def(null, Stage("a", Manager(1))) with { FallbackApprovers = Users(UserIds(n)) };
        (await Create(r, Input("f1", r.Confirmed, D(15)))).Ok.Should().BeTrue();
        await SetOverride(r, PolicyCatalog.ApprovalMaxApprovers, "10");
        (await Update(r, Input("f1", r.Confirmed, D(15), "改名"))).Ok.Should().BeTrue();
        (await Update(r, Input("f1", r.Confirmed, D(16)))).Text("definition.fallbackApprovers").Should().Contain("10");
    }

    [Fact]
    public async Task SaveFlow_PickMaxOverLoweredLimit_OnlyIncreaseBlocked()
    {
        var r = await BuildPurchase();
        ApprovalDefinition D(int max) => Def(null, Stage("p", new ApproverSource(ApprovalSources.InitiatorPick, CandidateGroupIds: [BoardGroup], Min: 1, Max: max)));
        (await Create(r, Input("f1", r.Confirmed, D(15)))).Ok.Should().BeTrue();
        await SetOverride(r, PolicyCatalog.ApprovalMaxApprovers, "10");
        (await Update(r, Input("f1", r.Confirmed, D(15), "改名"))).Ok.Should().BeTrue();
        (await Update(r, Input("f1", r.Confirmed, D(16)))).Text("definition.stages[0].approvers").Should().Contain("10");
    }

    [Fact]
    public async Task SaveFlow_DueHoursRaisedOverLimit_Returns400()
    {
        var r = await BuildPurchase();
        ApprovalDefinition D(int h) => Def(null, Stage("a", Manager(1), dueHours: h));
        (await Create(r, Input("f1", r.Confirmed, D(100)))).Ok.Should().BeTrue();
        await SetOverride(r, PolicyCatalog.ApprovalMaxDueHours, "48");
        (await Update(r, Input("f1", r.Confirmed, D(100), "改名"))).Ok.Should().BeTrue();
        (await Update(r, Input("f1", r.Confirmed, D(101)))).Text("definition.stages[0].dueHours").Should().Contain("48");
        (await Create(r, Input("f2", r.Confirmed, D(49)))).Text("definition.stages[0].dueHours").Should().Contain("48");
        (await Create(r, Input("f3", r.Confirmed, D(0)))).Text("definition.stages[0].dueHours").Should().NotBeEmpty();
    }

    [Fact]
    public async Task SaveFlow_ManagerLevelModuleOverride3_Level4_Returns400()
    {
        var r = await BuildPurchase();
        await SetOverride(r, PolicyCatalog.ApprovalMaxManagerLevel, "3");
        (await Create(r, Input("f1", r.Confirmed, Def(null, Stage("a", Manager(4)))))).Text("definition.stages[0].approvers").Should().Contain("3");
        (await Create(r, Input("f2", r.Confirmed, Def(null, Stage("a", Manager(3)))))).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task SaveFlow_ManagerLevelOtherModule_UsesSystem5_Succeeds()
    {
        var r = await BuildPurchase();
        var other = await BuildPurchase("pq");
        await SetOverride(r, PolicyCatalog.ApprovalMaxManagerLevel, "3");
        (await Create(other, Input("g1", other.Confirmed, Def(null, Stage("a", Manager(5)))))).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task SaveFlow_ManagerLevelOverrideWiderThanSystem_Succeeds()
    {
        var r = await BuildPurchase();
        await SetOverride(r, PolicyCatalog.ApprovalMaxManagerLevel, "8");
        (await Create(r, Input("f1", r.Confirmed, Def(null, Stage("a", Manager(7)))))).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task SaveFlow_ManagerLevelSystemLowered2_Level3_Returns400()
    {
        var r = await BuildPurchase();
        await SetSystem(PolicyCatalog.ApprovalMaxManagerLevel, "2");
        (await Create(r, Input("f1", r.Confirmed, Def(null, Stage("a", Manager(3)))))).Text("definition.stages[0].approvers").Should().Contain("2");
    }

    [Fact]
    public async Task SaveFlow_ManagerLevelOverLoweredLimit_Unchanged_Allowed()
    {
        var r = await BuildPurchase();
        (await Create(r, Input("f1", r.Confirmed, Def(null, Stage("a", Manager(4)))))).Ok.Should().BeTrue();
        await SetOverride(r, PolicyCatalog.ApprovalMaxManagerLevel, "3");
        var same = await Update(r, Input("f1", r.Confirmed, Def(null, Stage("a", Manager(4))), "改名"));
        same.Ok.Should().BeTrue(same.All);
        (await Update(r, Input("f1", r.Confirmed, Def(null, Stage("a", Manager(5)))))).Text("definition.stages[0].approvers").Should().Contain("3");
        // 既有流程：錯誤訊息說明舊版的數字仍允許；新建流程沒有舊版，不附這句
        (await Update(r, Input("f1", r.Confirmed, Def(null, Stage("a", Manager(5)))))).Text("definition.stages[0].approvers").Should().Contain("沿用原本的數字");
        (await Create(r, Input("f2", r.Confirmed, Def(null, Stage("a", Manager(4)))))).Text("definition.stages[0].approvers").Should().NotContain("沿用原本的數字");
    }

    [Fact]
    public async Task SaveFlow_FallbackManagerLevelOverLimit_Returns400()
    {
        var r = await BuildPurchase();
        await SetOverride(r, PolicyCatalog.ApprovalMaxManagerLevel, "3");
        var d = Def(null, Stage("a", Manager(1))) with { FallbackApprovers = Manager(4) };
        (await Create(r, Input("f1", r.Confirmed, d))).Text("definition.fallbackApprovers").Should().Contain("3");
    }

    [Fact]
    public async Task SaveFlow_FallbackManagerLevelOverLoweredLimit_Unchanged_Allowed()
    {
        var r = await BuildPurchase();
        ApprovalDefinition D(int level) => Def(null, Stage("a", Manager(1))) with { FallbackApprovers = Manager(level) };
        (await Create(r, Input("f1", r.Confirmed, D(4)))).Ok.Should().BeTrue();
        await SetOverride(r, PolicyCatalog.ApprovalMaxManagerLevel, "3");
        var same = await Update(r, Input("f1", r.Confirmed, D(4), "改名"));
        same.Ok.Should().BeTrue(same.All);
        (await Update(r, Input("f1", r.Confirmed, D(5)))).Text("definition.fallbackApprovers").Should().Contain("3");
    }

    // ───────── 執行期（Task 19）─────────

    private async Task<PurchaseRig> WithFlow(ApprovalDefinition d, string key = "po-x")
    {
        var r = await Scenario3();
        await SeedFlow(r, key, r.Confirmed, 0, d);
        return r;
    }

    private async Task<ServiceResult> SubmitSmall(PurchaseRig r, string user = "buyer1", Dictionary<string, List<string>>? picks = null, string? comment = null)
    {
        As(user);
        var po = await NewPo(r, price: 100);
        return await Send(new SubmitApprovalRequest(po.Id, r.Confirmed, Rec(po.Id).Row.RowVersion, picks, comment));
    }

    [Fact]
    public async Task Resolve_GroupOverModuleMaxApprovers_UsesPolicy()
    {
        var r = await WithFlow(Def(null, Stage("fin", Group(FinanceGroup))));
        await SetOverride(r, PolicyCatalog.ApprovalMaxApprovers, "1");
        var res = await SubmitSmall(r);
        res.IsSuccess.Should().BeFalse();
        res.Message.Should().Contain("超過 1 人");
    }

    [Fact]
    public async Task Resolve_StaticUsersOverLoweredLimit_UsesTechnicalMax()
    {
        await AddUsers(15);
        var r = await WithFlow(Def(null, Stage("fix", Users(UserIds(15)))));
        await SetOverride(r, PolicyCatalog.ApprovalMaxApprovers, "10");
        var res = await SubmitSmall(r);
        res.IsSuccess.Should().BeTrue(Dump(res));
        TasksOf(((SubmitApprovalResponse)res.Data!).InstanceId!.Value).Count(t => t.Status == ApprovalTaskStatus.Open).Should().Be(15);
    }

    [Fact]
    public async Task Resolve_StaticUsersOverTechnicalMax_Rejects()
    {
        await AddUsers(51);
        var r = await WithFlow(Def(null, Stage("fix", Users(UserIds(51)))));   // 繞過驗證直接種入
        var res = await SubmitSmall(r);
        res.IsSuccess.Should().BeFalse();
        res.Message.Should().Contain("超過 50 人");
    }

    [Fact]
    public async Task Resolve_LoweredAfterSubmit_NextStageUsesCurrent()
    {
        var r = await WithFlow(Def(null, Stage("mgr", Manager(1)), Stage("fin", Group(FinanceGroup))));
        var res = await SubmitSmall(r);
        res.IsSuccess.Should().BeTrue(Dump(res));
        var id = ((SubmitApprovalResponse)res.Data!).InstanceId!.Value;
        await SetOverride(r, PolicyCatalog.ApprovalMaxApprovers, "1");
        As("gm");
        var task = TasksOf(id).Single(t => t.AssigneeUserId == "gm" && t.Status == ApprovalTaskStatus.Open);
        var approve = await Send(new ApproveTaskRequest(task.Id, Inst(id).RowVersion, null, null));
        approve.IsSuccess.Should().BeFalse();
        approve.Message.Should().Contain("超過 1 人");
    }

    [Fact]
    public async Task Pick_OverPolicyLimit_Returns400()
    {
        await AddUsers(3);
        // 舊資料沒有 Max：落回政策上限
        var r = await WithFlow(Def(null, Stage("p", new ApproverSource(ApprovalSources.InitiatorPick, CandidateGroupIds: [], Min: 1, Max: null))));
        await SetOverride(r, PolicyCatalog.ApprovalMaxApprovers, "2");
        var res = await SubmitSmall(r, picks: new() { ["p"] = [.. UserIds(3)] });
        res.IsSuccess.Should().BeFalse();
        res.Errors.Should().ContainKey("picks.p");
        res.Errors["picks.p"][0].Should().Contain("2");
        (await SubmitSmall(r, picks: new() { ["p"] = [.. UserIds(2)] })).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]   // 流程沒設備援：政策預設（系統管理員）
    [InlineData(true)]    // 流程的備援是群組
    public async Task Resolve_StaticUsersWithOneDisabled_OverLoweredLimit_StillUsesTechnicalMax(bool groupFallback)
    {
        await AddUsers(16);
        Db.Context.Add(new AppUser { UserId = "sysadm", DisplayName = "sysadm", IsSystemAdmin = true });
        var disabled = await Db.Context.Set<AppUser>().FirstAsync(u => u.UserId == "u15");
        disabled.IsActive = false;
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        var def = Def(null, Stage("fix", Users(UserIds(15))));   // 固定 15 人（含已停用的 u15）
        var r = await WithFlow(groupFallback ? def with { FallbackApprovers = Group(FinanceGroup) } : def);
        await SetOverride(r, PolicyCatalog.ApprovalMaxApprovers, "10");
        var res = await SubmitSmall(r);
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    // 頂替的備援簽核人在執行時只受技術上限 50，不計入每關人數的當下上限；關卡本身的來源仍依當下上限

    [Fact]
    public async Task Resolve_PickFullAtLimit_OneDisabled_FallbackSubstitutes_Succeeds()
    {
        await AddUsers(20);
        var pick = new ApproverSource(ApprovalSources.InitiatorPick, CandidateGroupIds: [], Min: 1, Max: 20);
        var r = await WithFlow(Def(null, Stage("lead", Group(PurchaseLeadGroup)), Stage("p", pick)) with { FallbackApprovers = Users("fin1", "fin2") });
        var res = await SubmitSmall(r, picks: new() { ["p"] = [.. UserIds(20)] });   // 上限 20，選滿
        res.IsSuccess.Should().BeTrue(Dump(res));
        var id = ((SubmitApprovalResponse)res.Data!).InstanceId!.Value;
        (await Db.Context.Set<AppUser>().FirstAsync(u => u.UserId == "u20")).IsActive = false;
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();

        As("buyer2");
        var task = TasksOf(id).Single(t => t.AssigneeUserId == "buyer2" && t.Status == ApprovalTaskStatus.Open);
        var approve = await Send(new ApproveTaskRequest(task.Id, Inst(id).RowVersion, null, null));
        approve.IsSuccess.Should().BeTrue(Dump(approve));   // 19 人 + 2 位頂替的備援 = 21，備援不計入 20
        TasksOf(id).Where(t => t.Status == ApprovalTaskStatus.Open).Select(t => t.AssigneeUserId).Should().Contain(["fin1", "fin2"]);
    }

    [Fact]
    public async Task Resolve_GroupMembersAllGone_FixedFallbackOverLoweredLimit_Succeeds()
    {
        await AddUsers(15);
        var r = await WithFlow(Def(null, Stage("board", Group(BoardGroup))) with { FallbackApprovers = Users(UserIds(15)) });
        Db.Context.RemoveRange(Db.Context.Set<UserGroupMapping>().Where(m => m.GroupId == BoardGroup));   // 群組成員全部調走
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        await SetOverride(r, PolicyCatalog.ApprovalMaxApprovers, "10");
        var res = await SubmitSmall(r);
        res.IsSuccess.Should().BeTrue(Dump(res));
        TasksOf(((SubmitApprovalResponse)res.Data!).InstanceId!.Value).Count(t => t.Status == ApprovalTaskStatus.Open).Should().Be(15);
    }

    [Fact]
    public async Task Resolve_GroupSourceOverLimit_WithFixedFallback_StillReturns409()
    {
        await AddUsers(15);
        var r = await WithFlow(Def(null, Stage("fin", Group(FinanceGroup))) with { FallbackApprovers = Users(UserIds(15)) });
        await SetOverride(r, PolicyCatalog.ApprovalMaxApprovers, "1");   // 群組本身 2 人
        var res = await SubmitSmall(r);
        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Contain("超過 1 人");
    }

    [Fact]
    public async Task Resolve_UnmatchedPickAll_MinUsesEffectiveRange_FallbackFillsSeats()
    {
        await AddUsers(10);
        // 存檔最少 12、最多 15 的會簽自選，候選群組失效；上限調成 10、備援固定 10 人
        var pick = new ApproverSource(ApprovalSources.InitiatorPick, CandidateGroupIds: [], Min: 12, Max: 15) with { CandidatesUnmatched = true };
        var r = await WithFlow(Def(null, Stage("p", pick, mode: ApprovalModes.All)) with { FallbackApprovers = Users(UserIds(10)) });
        await SetOverride(r, PolicyCatalog.ApprovalMaxApprovers, "10");
        var res = await SubmitSmall(r);
        res.IsSuccess.Should().BeTrue(Dump(res));
        var tasks = TasksOf(((SubmitApprovalResponse)res.Data!).InstanceId!.Value).Where(t => t.Status == ApprovalTaskStatus.Open).ToList();
        tasks.Select(t => t.AssigneeUserId).Distinct().Should().HaveCount(10);
        tasks.Select(t => t.SeatKey).Distinct().Should().HaveCount(10);   // 有效最少人數 10 → 10 個備援席位，共用同一份 10 人名單
    }

    [Fact]
    public async Task Resolve_FallbackUsersOver_LoweredLimit_UsesTechnicalMax()
    {
        await AddUsers(51);
        var r = await WithFlow(Def(null, Stage("fix", Users(UserIds(1)))));
        await SetOverride(r, PolicyCatalog.ApprovalMaxApprovers, "10");
        var ctx = await Svc<ApproverResolver>().ContextAsync((await Svc<IModuleRepository>().GetByIdAsync(r.ModuleId, true, default))!, "buyer1", new Dictionary<string, System.Text.Json.JsonElement?>(), null, default);
        var viaUsers = ApproverResolver.ResolveFallback(ctx, Users(UserIds(15)));
        viaUsers.Error.Should().BeNull();
        viaUsers.Seats.Single().UserIds.Should().HaveCount(15);
        // 備援在執行時只受技術上限 50（不分來源）
        ApproverResolver.ResolveFallback(ctx, Users(UserIds(51))).Error.Should().NotBeNull();
    }

    // 自選關卡：有效上限＝min(存檔上限, 當下上限)，最少人數大於有效上限時改用有效上限
    private ApprovalDefinition PickFlow() =>
        Def(null, Stage("p", new ApproverSource(ApprovalSources.InitiatorPick, CandidateGroupIds: [], Min: 12, Max: 15)));

    [Fact]
    public async Task Pick_LoweredLimit_PreviewShowsEffectiveRange()
    {
        await AddUsers(15);
        var r = await WithFlow(PickFlow());
        await SetOverride(r, PolicyCatalog.ApprovalMaxApprovers, "10");
        As("buyer1");
        var po = await NewPo(r, price: 100);
        var res = await Send(new PreviewRecordApprovalRequest(po.Id, r.Confirmed));
        res.IsSuccess.Should().BeTrue(Dump(res));
        var pick = ((ApprovalPreviewResponse)res.Data!).Picks.Single();
        (pick.Min, pick.Max).Should().Be((10, 10));
    }

    [Fact]
    public async Task Pick_LoweredLimit_TenSucceeds_ElevenReturns400()
    {
        await AddUsers(15);
        var r = await WithFlow(PickFlow());
        await SetOverride(r, PolicyCatalog.ApprovalMaxApprovers, "10");
        var over = await SubmitSmall(r, picks: new() { ["p"] = [.. UserIds(11)] });
        over.IsSuccess.Should().BeFalse();
        over.Errors.Should().ContainKey("picks.p");
        over.Errors["picks.p"][0].Should().Contain("10").And.Be("請選 10 位簽核人");   // 最少最多相同時不寫「10 到 10」
        var ok = await SubmitSmall(r, picks: new() { ["p"] = [.. UserIds(10)] });
        ok.IsSuccess.Should().BeTrue(Dump(ok));
    }

    private async Task<(PurchaseRig R, long Inst, long Task)> PendingForBuyer2()
    {
        var r = await Scenario3();
        As("buyer1");
        var po = await NewPo(r, price: 20000);   // 流程 B：purchase-lead 群組（buyer2）
        var id = (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;
        return (r, id, TasksOf(id).Single(t => t.AssigneeUserId == "buyer2").Id);
    }

    [Fact]
    public async Task Comment_OverSystemLimit_Returns400_AllFourActions()
    {
        var (r, id, taskId) = await PendingForBuyer2();
        await SetSystem(PolicyCatalog.ApprovalMaxCommentLength, "10");
        var eleven = new string('字', 11);

        As("buyer2");
        var approve = await Send(new ApproveTaskRequest(taskId, Inst(id).RowVersion, eleven, null));
        approve.Errors["comment"][0].Should().Contain("10");
        var reject = await Send(new RejectTaskRequest(taskId, Inst(id).RowVersion, eleven, RejectTaskHandler.ToInitiator));
        reject.Errors["comment"][0].Should().Contain("10");
        As("buyer1");
        var withdraw = await Send(new WithdrawApprovalRequest(id, Inst(id).RowVersion, eleven));
        withdraw.Errors["comment"][0].Should().Contain("10");
        var submit = await SubmitSmall(r, comment: eleven);
        submit.Errors["comment"][0].Should().Contain("10");

        // 剛好 10 字可以
        (await Send(new WithdrawApprovalRequest(id, Inst(id).RowVersion, new string('字', 10)))).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("submit")]
    public async Task Comment_ExactlyAtLimit_Allowed(string action)
    {
        var ten = new string('字', 10);
        var (r, id, taskId) = await PendingForBuyer2();
        await SetSystem(PolicyCatalog.ApprovalMaxCommentLength, "10");
        ServiceResult res;
        if (action == "submit") res = await SubmitSmall(r, comment: ten);
        else
        {
            As("buyer2");
            res = action == "approve"
                ? await Send(new ApproveTaskRequest(taskId, Inst(id).RowVersion, ten, null))
                : await Send(new RejectTaskRequest(taskId, Inst(id).RowVersion, ten, RejectTaskHandler.ToInitiator));
        }
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task Comment_FactoryLimit_1000()
    {
        var (_, id, _) = await PendingForBuyer2();
        As("buyer1");
        var over = await Send(new WithdrawApprovalRequest(id, Inst(id).RowVersion, new string('a', 1001)));
        over.Errors["comment"][0].Should().Contain("1000");
        (await Send(new WithdrawApprovalRequest(id, Inst(id).RowVersion, new string('a', 1000)))).IsSuccess.Should().BeTrue();
    }
}
