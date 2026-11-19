using System.Text.Json;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using static DynamicModules.ApplicationTests.Dynamic.Approvals.ApprovalGraphTestKit;
using DynamicModules.TestSupport;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>設計器的乾跑驗證（fg3a Task 2，J64、J68）：跑與發布相同的檢查、回傳錯誤與警告與是否開新版本，不寫入。</summary>
public class ApprovalFlowValidateTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private static ApprovalFlowDraftInput Draft(string key, long to, int priority, ApprovalDefinition d, JsonElement? layout = null) =>
        new(key, Text(key), to, priority, d, layout);

    private async Task<PurchaseRig> Rig()
    {
        var r = await BuildPurchase();
        await GrantEveryone(r.Module);
        As("admin");
        return r;
    }

    private async Task<ApprovalFlowValidation> Validate(PurchaseRig r, ApprovalFlowDraftInput input, string? key = null)
    {
        var res = await Send(new ValidateApprovalFlowRequest(r.Module, key, input));
        res.IsSuccess.Should().BeTrue(Dump(res));
        return (ApprovalFlowValidation)res.Data!;
    }

    /// <summary>設計 §7.1 的範例（通知節點內容用純文字）；notifyAsApproval 時把 tell-hr 換成簽核節點。</summary>
    private ApprovalDefinition DesignExample(PurchaseRig r, bool notifyAsApproval = false) => G(
        [Start(), Approval("mgr", Manager(1)), Condition("amount"), Fork("split"), Approval("legal", Group(PurchaseLeadGroup)),
         Approval("fin", Group(FinanceGroup)), Join("merge", "split"),
         notifyAsApproval ? Approval("tell-hr", Users("fin2")) : Notify("tell-hr"), End()],
        [E("e1", "start", "mgr"), E("e2", "mgr", "amount"), E("e3", "amount", "split", Cond(r.Total, "gte", 100000), order: 1),
         E("e4", "amount", "tell-hr", isDefault: true), E("e5", "split", "legal"), E("e6", "split", "fin"), E("e7", "legal", "merge"),
         E("e8", "fin", "merge"), E("e9", "merge", "tell-hr"), E("e10", "tell-hr", "end")]);

    private static ApprovalDefinition Linear(int n) => G(
        [Start(), .. Enumerable.Range(1, n).Select(i => Approval($"s{i}", Users("fin1"))), End()],
        [E("e0", "start", "s1"), .. Enumerable.Range(1, n - 1).Select(i => E($"e{i}", $"s{i}", $"s{i + 1}")), E($"e{n}", $"s{n}", "end")]);

    [Fact]
    public async Task Validate_DesignExample_OnlyNotifyNotYetError()
    {
        var r = await Rig();

        var got = await Validate(r, Draft("po-g", r.Confirmed, 1, DesignExample(r)));

        got.Errors.Keys.Should().Equal("definition.nodes.tell-hr");
        got.Errors["definition.nodes.tell-hr"].Should().Equal(ApprovalMessages.NotifyNodeNotYet);
        got.Warnings.Should().BeEmpty("有錯誤時不算警告");
    }

    [Fact]
    public async Task Validate_DesignExampleNotifyAsApproval_NoErrorsNoWarnings()
    {
        var r = await Rig();

        var got = await Validate(r, Draft("po-g", r.Confirmed, 1, DesignExample(r, notifyAsApproval: true)));

        got.Errors.Should().BeEmpty();
        got.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_DefaultEdgeStraightToEnd_OnlyW2()
    {
        var r = await Rig();
        var def = G([Start(), Approval("a", Users("fin1")), Condition("c"), Approval("b", Users("fin2")), End()],
            [E("e0", "start", "a"), E("e1", "a", "c"), E("e2", "c", "b", Cond(r.Total, "gte", 1), order: 1),
             E("e3", "c", "end", isDefault: true), E("e4", "b", "end")]);

        var got = await Validate(r, Draft("po-g", r.Confirmed, 1, def));

        got.Errors.Should().BeEmpty();
        got.Warnings.Should().ContainSingle().Which.Should().Be(
            new ApprovalFlowWarning("definition.nodes.c", "W2", ApprovalMessages.Graph("defaultToEndWithoutApproval")));
    }

    [Fact]
    public async Task Messages_EveryWarningCodeHasText()
    {
        var r = await Rig();
        // W1：任一到齊、有一條空分支；W3：條件出線上的簽核節點有略過條件；W2：預設線直接到結束
        var def = G(
            [Start(), Approval("a", Users("fin1")), Condition("c"), Approval("skip", Users("fin2"), when: Cond(r.Total, "gte", 5)),
             Fork("f"), Approval("x", Users("fin1")), Join("j", "f", ApprovalModes.Any), End()],
            [E("e0", "start", "a"), E("e1", "a", "c"), E("e2", "c", "skip", Cond(r.Total, "gte", 1), order: 1), E("e3", "c", "end", isDefault: true),
             E("e4", "skip", "f"), E("e5", "f", "x"), E("e6", "f", "j"), E("e7", "x", "j"), E("e8", "j", "end")]);

        var got = await Validate(r, Draft("po-g", r.Confirmed, 1, def));

        got.Errors.Should().BeEmpty();
        got.Warnings.Select(w => w.Code).Should().BeEquivalentTo("W1", "W2", "W3");
        got.Warnings.Should().OnlyContain(w => !w.Message.StartsWith("流程圖結構不正確") && w.Key.StartsWith("definition.nodes."));
    }

    [Fact]
    public async Task Validate_InvalidGraph_ErrorsKeyedByNodeId()
    {
        var r = await Rig();
        var broken = G([Start(), Condition("c"), Approval("a", Users("fin1")), Approval("x", Users("emp1")), End()],
            [E("e0", "start", "c"), E("ca", "c", "a", Cond(r.Total, "gte", 1), order: 1), E("cb", "c", "end", Cond(r.Total, "gte", 2), order: 2),
             E("ae", "a", "end"), E("xe", "x", "end")]);

        var got = await Validate(r, Draft("po-g", r.Confirmed, 1, broken));

        got.Errors.Keys.Should().Contain("definition.nodes.x");
        got.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_StructuralError_NoWarnings()
    {
        var r = await Rig();
        // 預設線直接到結束（W2）同時有環（G5）
        var def = G([Start(), Approval("a", Users("fin1")), Condition("c"), Approval("b", Users("fin2")), End()],
            [E("e0", "start", "a"), E("e1", "a", "c"), E("e2", "c", "b", Cond(r.Total, "gte", 1), order: 1),
             E("e3", "c", "end", isDefault: true), E("e4", "b", "a")]);

        var got = await Validate(r, Draft("po-g", r.Confirmed, 1, def));

        got.Errors.Should().NotBeEmpty();
        got.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_RunsRowChecksAndConstraints()
    {
        var r = await Rig();
        await SeedGraphFlow(r, "po-a", r.Confirmed, 1, Linear(1));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();

        var dupPriority = await Validate(r, Draft("po-g", r.Confirmed, 1, Linear(1)));
        var badTarget = await Validate(r, Draft("po-g", r.Draft, 2, Linear(1)));
        // 退回狀態讓「草稿 → 已確認」沒有可退回的路：入口約束 flows[po-g]
        var constraint = await Validate(r, Draft("po-g", r.Confirmed, 2, Linear(1) with { RejectTo = r.Cancelled }));

        dupPriority.Errors.Keys.Should().Equal("priority");
        badTarget.Errors.Keys.Should().Contain("toOptionId");
        constraint.Errors.Keys.Should().Contain("flows[po-g]");
    }

    [Fact]
    public async Task Validate_WithKey_UsesCurrentVersionForTightenedLimits()
    {
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear(12));   // 政策上限 10，舊版直接寫入 12 個簽核節點

        var same = await Validate(r, Draft("po-g", r.Confirmed, 1, Linear(12)), key: "po-g");
        var grow = await Validate(r, Draft("po-g", r.Confirmed, 1, Linear(13)), key: "po-g");
        var asNew = await Validate(r, Draft("po-new", r.Confirmed, 2, Linear(12)));

        same.Errors.Should().BeEmpty();
        grow.Errors.Keys.Should().Contain("definition.graph");
        asNew.Errors.Keys.Should().Contain("definition.graph");
    }

    [Fact]
    public async Task Validate_KeyOfOtherModule_400()
    {
        var r = await Rig();
        var other = await BuildPurchase("pq");
        await SeedGraphFlow(other, "pq-a", other.Confirmed, 1, Linear(1));

        var res = await Send(new ValidateApprovalFlowRequest(r.Module, "pq-a", Draft("pq-a", r.Confirmed, 1, Linear(1))));
        var missing = await Send(new ValidateApprovalFlowRequest(r.Module, "nope", Draft("nope", r.Confirmed, 1, Linear(1))));
        var renamed = await Send(new ValidateApprovalFlowRequest(other.Module, "pq-a", Draft("pq-b", other.Confirmed, 1, Linear(1))));

        res.IsInvalid.Should().BeTrue(Dump(res));
        res.Errors["key"].Should().Equal(ApprovalMessages.FlowNotInModule);
        missing.Errors.Keys.Should().Equal("key");
        renamed.Errors.Keys.Should().Equal("key");
    }

    [Fact]
    public async Task Validate_NewFlow_KeyTaken_ReportsKeyError()
    {
        var r = await Rig();
        await SeedGraphFlow(r, "po-a", r.Confirmed, 1, Linear(1));

        var got = await Validate(r, Draft("po-a", r.Confirmed, 2, Linear(1)));

        got.Errors["key"].Should().Equal(ApprovalMessages.FlowKeyTaken);
    }

    [Fact]
    public async Task Validate_SequenceFlow_ReportsNewVersion()
    {
        var r = await Rig();
        var seq = Def(null, Stage("lead", Group(PurchaseLeadGroup)));
        var flow = await SeedFlow(r, "po-a", r.Confirmed, 1, seq);
        await SeedInstance(r, flow);

        var got = await Validate(r, Draft("po-a", r.Confirmed, 1, ApprovalGraphConversion.FromSequence(seq)), key: "po-a");

        got.Errors.Should().BeEmpty();
        (got.NewVersion, got.NextVersion, got.PendingCount).Should().Be((true, 2, 1));
    }

    [Fact]
    public async Task Validate_UnusedVersion_NoNewVersion()
    {
        var r = await Rig();
        var seq = Def(null, Stage("lead", Group(PurchaseLeadGroup)));
        await SeedFlow(r, "po-a", r.Confirmed, 1, seq);

        var got = await Validate(r, Draft("po-a", r.Confirmed, 1, ApprovalGraphConversion.FromSequence(seq)), key: "po-a");

        (got.NewVersion, got.NextVersion, got.PendingCount).Should().Be((false, null, 0));
    }

    [Fact]
    public async Task Validate_LayoutOnly_NoNewVersion()
    {
        var r = await Rig();
        var flow = await SeedGraphFlow(r, "po-a", r.Confirmed, 1, Linear(2));
        await SeedInstance(r, flow);
        var layout = JsonSerializer.SerializeToElement(new { nodes = new { start = new { x = 10, y = 20 } } });

        var got = await Validate(r, Draft("po-a", r.Confirmed, 1, Linear(2), layout), key: "po-a");
        var changed = await Validate(r, Draft("po-a", r.Confirmed, 1, Linear(3)), key: "po-a");

        got.NewVersion.Should().BeFalse();
        changed.NewVersion.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_UsedFlow_Retarget_ReportsToOptionId()
    {
        var r = await Rig();
        var flow = await SeedGraphFlow(r, "po-a", r.Confirmed, 1, Linear(1));
        await SeedInstance(r, flow);

        var got = await Validate(r, Draft("po-a", r.Cancelled, 1, Linear(1)), key: "po-a");

        got.Errors["toOptionId"].Should().Equal(ApprovalFlowEditor.UsedCannotRetarget);
    }

    [Fact]
    public async Task Validate_DoesNotWrite()
    {
        var r = await Rig();
        var flow = await SeedGraphFlow(r, "po-a", r.Confirmed, 1, Linear(1));
        var before = Flow("po-a");

        await Validate(r, Draft("po-a", r.Confirmed, 5, Linear(2)), key: "po-a");
        await Validate(r, Draft("po-new", r.Confirmed, 2, Linear(1)));

        Flow("po-a").Should().BeEquivalentTo(before);
        Db.Context.Set<ApprovalFlow>().AsNoTracking().Count().Should().Be(1);
        Db.Context.Set<ApprovalFlowVersion>().AsNoTracking().Count().Should().Be(1);
        flow.Id.Should().Be(before.Id);
    }

    [Fact]
    public async Task Validate_SequenceDefinition_Rejected400()
    {
        var r = await Rig();

        var res = await Send(new ValidateApprovalFlowRequest(r.Module, null, Draft("po-g", r.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup))))));

        res.IsInvalid.Should().BeTrue(Dump(res));
        res.Errors["definition.shape"].Should().Equal(ApprovalMessages.DraftShapeInvalid);
    }

    [Fact]
    public async Task Validate_TooLarge_400()
    {
        var r = await Rig();
        var big = G([new ApprovalNodeDef("start", ApprovalNodeKinds.Start, Text(new string('x', ApprovalGraphLimits.MaxDraftBytes))), End()],
            [E("e0", "start", "end")]);

        var res = await Send(new ValidateApprovalFlowRequest(r.Module, null, Draft("po-g", r.Confirmed, 1, big)));

        res.IsInvalid.Should().BeTrue(Dump(res));
        res.Errors["definition"].Should().Equal(ApprovalMessages.DraftTooLarge);
    }

    [Fact]
    public async Task Validate_BadLayout_400()
    {
        var r = await Rig();
        var layout = JsonSerializer.SerializeToElement(new { nodes = new { start = new { x = 999999, y = 0 } } });

        var res = await Send(new ValidateApprovalFlowRequest(r.Module, null, Draft("po-g", r.Confirmed, 1, Linear(1), layout)));

        res.Errors["layout"].Should().Equal(ApprovalMessages.LayoutInvalid);
    }

    [Fact]
    public async Task Validate_ReadOnlyDesigner_Forbidden()
    {
        var r = await Rig();
        Db.Context.Add(new UserProgramPrivilege { UserId = "emp1", ProgramCode = "DYN00000", CanRead = true });
        await Db.Context.SaveForTestsAsync();
        As("emp1");

        var res = await Send(new ValidateApprovalFlowRequest(r.Module, null, Draft("po-g", r.Confirmed, 1, Linear(1))));

        res.IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_DetailModule_400()
    {
        var r = await Rig();

        var res = await Send(new ValidateApprovalFlowRequest(r.Line, null, Draft("po-g", r.Confirmed, 1, Linear(1))));

        res.Errors.Should().ContainKey("moduleKey");
    }

    [Fact]
    public async Task Validate_PriorityComesFromCurrentFlow_LikePublish()
    {
        // 目標狀態沒變：優先序沿用流程目前的值（與發布相同），請求帶的值不參與，所以不會報「優先序重複」（審查 🟡1）
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear(1));
        await SeedGraphFlow(r, "po-h", r.Confirmed, 2, Linear(1));
        var input = Draft("po-g", r.Confirmed, 2, Linear(2));

        var got = await Validate(r, input, key: "po-g");
        var published = await Send(new PublishApprovalFlowRequest("po-g", input with { BaseVersionId = Flow("po-g").CurrentVersionId }));

        got.Errors.Should().BeEmpty();
        published.IsSuccess.Should().BeTrue(Dump(published));
        Flow("po-g").Priority.Should().Be(1);
    }

    [Fact]
    public async Task Validate_Retarget_TakesRequestedPriority_LikePublish()
    {
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear(1));
        await SeedGraphFlow(r, "po-c", r.Cancelled, 5, Linear(1));

        var dup = await Validate(r, Draft("po-g", r.Cancelled, 5, Linear(1)), key: "po-g");

        dup.Errors.Keys.Should().Equal("priority");
    }
}
