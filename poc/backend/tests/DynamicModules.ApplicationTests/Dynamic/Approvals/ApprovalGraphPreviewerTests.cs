using System.Text.Json;
using System.Text.Json.Serialization;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.ApplicationTests.Dynamic.Approvals.ApprovalGraphTestKit;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>流程圖的送出前預覽與設計器試算（計畫 fg2e：J19、J26、U9、U10、U12）：同一套比對與解析，不寫入。</summary>
public class ApprovalGraphPreviewerTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    protected override void ConfigureServices(IServiceCollection services) => OpenGate(services);

    private long EveryoneGroup() => Db.Context.Set<PermissionGroup>().AsNoTracking().Single(g => g.Key == "everyone").Id;

    /// <summary>草稿 → 已確認需簽核（沒有流程符合就擋），已確認只有這一條流程圖。</summary>
    private async Task<PurchaseRig> WithGraph(Func<PurchaseRig, ApprovalDefinition> def)
    {
        var r = await BuildPurchase();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, def(r));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module);
        return r;
    }

    private async Task<ApprovalPreviewResponse> Preview(PurchaseRig r, string user = "buyer1", decimal price = 5000)
    {
        As(user);
        var po = await NewPo(r, price: price);
        var res = await Send(new PreviewRecordApprovalRequest(po.Id, r.Confirmed));
        res.IsSuccess.Should().BeTrue(Dump(res));
        return (ApprovalPreviewResponse)res.Data!;
    }

    private async Task<ApprovalPreviewResponse> Designer(Dictionary<string, JsonElement?>? values)
    {
        As("admin");
        var res = await Send(new PreviewApprovalFlowRequest("po-g", null, values, "buyer1"));
        res.IsSuccess.Should().BeTrue(Dump(res));
        return (ApprovalPreviewResponse)res.Data!;
    }

    private int AdminAlerts() => Db.Context.Set<Notification>().AsNoTracking().Count(n => n.Kind == NotificationKinds.ApprovalAdminAlert);

    private static string[] Approvers(PreviewStage s) => [.. s.Approvers.Select(u => u.UserId)];

    // ───────── 預計路徑 ─────────

    [Fact]
    public async Task Preview_Graph_StagesFollowPredictedPath_PathReturned()
    {
        var r = await WithGraph(x => G(
            [Start(), Condition("amount"), Approval("big", Users("gm")), Approval("small", Users("emp1")), End(), End("end2")],
            [E("e1", "start", "amount"), E("e2", "amount", "big", Cond(x.Total, "gte", 10000), order: 1), E("e3", "amount", "small", isDefault: true),
             E("e4", "big", "end"), E("e5", "small", "end2")]));

        var p = await Preview(r, price: 50000);

        p.Outcome.Should().Be(ApprovalPreviewOutcomes.Flow);
        p.Stages.Select(s => (s.Code, s.Status)).Should().Equal(("big", PreviewStatuses.Enter));
        Approvers(p.Stages.Single()).Should().Equal("gm");
        p.Path!.NodeIds.Should().Equal("start", "amount", "big", "end");
        p.Path.EdgeIds.Should().Equal("e1", "e2", "e4");

        var small = await Preview(r, price: 5000);
        small.Stages.Select(s => s.Code).Should().Equal("small");
        small.Path!.EdgeIds.Should().Equal("e1", "e3", "e5");
    }

    [Fact]
    public async Task Preview_Graph_HiddenConditionNode_ConditionalThenPostdominator()
    {
        // U9 的例子：「主管 → 金額（引用發起人看不到的折扣）→ 大：財務、小：直接 → 總經理 → 結束」
        var r = await WithGraph(x => G(
            [Start(), Approval("mgr", Users("emp1")), Condition("amount"), Approval("fin", Users("fin1")), Approval("gm", Users("gm")), End()],
            [E("e1", "start", "mgr"), E("e2", "mgr", "amount"), E("e3", "amount", "fin", Cond(x.Discount, "gt", 0), order: 1),
             E("e4", "amount", "gm", isDefault: true), E("e5", "fin", "gm"), E("e6", "gm", "end")]));
        await HideFor(r.Module, r.Discount, EveryoneGroup());

        var p = await Preview(r);

        p.Stages.Select(s => (s.Code, s.Status)).Should().Equal(("mgr", PreviewStatuses.Enter), ("amount", PreviewStatuses.Conditional), ("gm", PreviewStatuses.Enter));
        p.Stages[1].Approvers.Should().BeEmpty();
        p.Path!.NodeIds.Should().NotContain("fin");
        JsonSerializer.Serialize(p).Should().NotContain($"f_{r.Discount}").And.NotContain("\"when\"");
    }

    [Fact]
    public async Task Preview_Graph_AnyJoinGrouped()
    {
        var r = await WithGraph(_ => G(
            [Start(), Approval("mgr", Users("emp1")), Fork("f"), Approval("legal", Users("emp2")), Approval("fin", Users("fin1")), Join("merge", "f", ApprovalModes.Any), End()],
            [E("e1", "start", "mgr"), E("e2", "mgr", "f"), E("e3", "f", "legal"), E("e4", "f", "fin"), E("e5", "legal", "merge"), E("e6", "fin", "merge"),
             E("e7", "merge", "end")]));

        var p = await Preview(r);

        p.Stages.Select(s => (s.Code, s.AnyGroup)).Should().Equal(("mgr", null), ("legal", "merge"), ("fin", "merge"));
        p.Path!.EdgeIds.Should().Contain(["e3", "e4", "e5", "e6"]);
    }

    [Fact]
    public async Task Preview_Graph_AllJoin_NoAnyGroup()
    {
        var r = await WithGraph(_ => G(
            [Start(), Fork("f"), Approval("legal", Users("emp2")), Approval("fin", Users("fin1")), Join("merge", "f"), End()],
            [E("e1", "start", "f"), E("e2", "f", "legal"), E("e3", "f", "fin"), E("e4", "legal", "merge"), E("e5", "fin", "merge"), E("e6", "merge", "end")]));

        var p = await Preview(r);

        p.Stages.Select(s => (s.Code, s.AnyGroup)).Should().Equal(("legal", null), ("fin", null));
    }

    private static ApprovalDefinition AnyJoinWithConditionalFin(long total) => G(
        [Start(), Approval("mgr", Users("emp1")), Fork("f"), Approval("legal", Users("emp2")),
         Approval("fin", Users("fin1"), when: Cond(total, "gte", 10000)), Join("merge", "f", ApprovalModes.Any), End()],
        [E("e1", "start", "mgr"), E("e2", "mgr", "f"), E("e3", "f", "legal"), E("e4", "f", "fin"), E("e5", "legal", "merge"), E("e6", "fin", "merge"),
         E("e7", "merge", "end")]);

    [Fact]
    public async Task Preview_Graph_AnyJoinImmediate_OnlyAfterJoin()
    {
        // U10 的例子：財務那條「金額小於一萬就略過」，五千元的單送出當下就通過匯合：預覽只看到主管
        var r = await WithGraph(x => AnyJoinWithConditionalFin(x.Total));

        var p = await Preview(r, price: 5000);

        p.Stages.Select(s => s.Code).Should().Equal("mgr");
        p.Path!.EdgeIds.Should().NotContain(["e3", "e5"]);
        JsonSerializer.Serialize(p).Should().NotContain("legal");
    }

    [Fact]
    public async Task Designer_Graph_AnyJoinImmediate_SkippedListed()
    {
        var r = await WithGraph(x => AnyJoinWithConditionalFin(x.Total));

        var p = await Designer(V((r.Total, 5000)));

        p.Stages.Select(s => (s.Code, s.Status)).Should().Equal(("mgr", PreviewStatuses.Enter), ("fin", PreviewStatuses.Skip));

        var big = await Designer(V((r.Total, 50000)));
        big.Stages.Select(s => (s.Code, s.Status, s.AnyGroup)).Should().Equal(
            ("mgr", PreviewStatuses.Enter, null), ("legal", PreviewStatuses.Enter, "merge"), ("fin", PreviewStatuses.Enter, "merge"));
    }

    [Fact]
    public async Task Preview_Graph_PickListsAllPickNodes()
    {
        // 自選節點不論在不在預計路徑上都要列人選要求（送出驗證要求每一個）
        var pick = new ApproverSource(ApprovalSources.InitiatorPick, CandidateGroupIds: [FinanceGroup], Min: 1, Max: 1);
        var r = await WithGraph(x => G(
            [Start(), Condition("c"), Approval("on", pick), Approval("off", pick), End(), End("end2")],
            [E("e1", "start", "c"), E("e2", "c", "on", Cond(x.Total, "gte", 10000), order: 1), E("e3", "c", "off", isDefault: true),
             E("e4", "on", "end"), E("e5", "off", "end2")]));

        var p = await Preview(r, "fin1", price: 50000);

        p.Stages.Select(s => (s.Code, s.Status)).Should().Equal(("on", PreviewStatuses.Pick));
        p.Picks.Select(x => x.StageCode).Should().BeEquivalentTo(["on", "off"]);
    }

    [Fact]
    public async Task Designer_Graph_SkippedNodesListed_ProblemsFilled()
    {
        var nobody = new PermissionGroup { Key = "nobody", NameI18n = Text("nobody") };
        Db.Context.Add(nobody);
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        var r = await WithGraph(x => G(
            [Start(), Approval("skip", Users("emp1"), when: Cond(x.Discount, "gt", 0)), Approval("ok", Group(PurchaseLeadGroup)),
             Approval("none", Group(nobody.Id)), End()],
            [E("e1", "start", "skip"), E("e2", "skip", "ok"), E("e3", "ok", "none"), E("e4", "none", "end")]) with { FallbackApprovers = Group(nobody.Id) });

        var p = await Designer(V((r.Discount, 0)));

        p.Stages.Select(s => (s.Code, s.Status, s.Problem)).Should().Equal(
            ("skip", PreviewStatuses.Skip, null), ("ok", PreviewStatuses.Enter, null), ("none", PreviewStatuses.Enter, PreviewProblems.NoApprover));
        p.FlowKey.Should().Be("po-g");
    }

    // ───────── 結構不合法與無法判斷 ─────────

    [Fact]
    public async Task Preview_Graph_ConditionUndecidable_409()
    {
        // 條件引用的欄位已不在：不走預設線，回「條件無法判斷」，預覽不登記異常通知
        var r = await WithGraph(_ => G(
            [Start(), Condition("c"), Approval("a", Users("emp1")), Approval("b", Users("emp2")), End(), End("end2")],
            [E("e1", "start", "c"), E("e2", "c", "a", Cond(999999, "gt", 1), order: 1), E("e3", "c", "b", isDefault: true), E("e4", "a", "end"), E("e5", "b", "end2")]));
        As("buyer1");
        var po = await NewPo(r);

        var res = await Send(new PreviewRecordApprovalRequest(po.Id, r.Confirmed));

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.FlowConditionUndecidable("po-g").Resolve(null));
        AdminAlerts().Should().Be(0);
    }

    private ApprovalDefinition NoDefaultEdge() => G(
        [Start(), Condition("c"), Approval("a", Users("emp1")), End()],
        [E("e1", "start", "c"), E("e2", "c", "a", Cond(1, "gt", 1), order: 1), E("e3", "a", "end")]);

    [Fact]
    public async Task Preview_Graph_BrokenGraph_409_PreviewInvalid_NoAlert()
    {
        var r = await WithGraph(_ => NoDefaultEdge());
        As("buyer1");
        var po = await NewPo(r);

        var res = await Send(new PreviewRecordApprovalRequest(po.Id, r.Confirmed));

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.GraphPreviewInvalid("po-g").Resolve(null));
        AdminAlerts().Should().Be(0);
    }

    [Fact]
    public async Task Designer_Graph_BrokenGraph_409_PreviewInvalid()
    {
        await WithGraph(_ => NoDefaultEdge());
        As("admin");

        var res = await Send(new PreviewApprovalFlowRequest("po-g", null, null, "buyer1"));

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.GraphPreviewInvalid("po-g").Resolve(null));
        AdminAlerts().Should().Be(0);
    }

    [Fact]
    public async Task Preview_Graph_NotifyNode_409_NoAlert()
    {
        // 通知節點在 fg5 之前引擎不支援：結構合法也一樣回預覽專用訊息
        var r = await WithGraph(_ => G(
            [Start(), Approval("a", Users("emp1")), Notify("tell"), End()],
            [E("e1", "start", "a"), E("e2", "a", "tell"), E("e3", "tell", "end")]));
        As("buyer1");
        var po = await NewPo(r);

        var res = await Send(new PreviewRecordApprovalRequest(po.Id, r.Confirmed));

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.GraphPreviewInvalid("po-g").Resolve(null));
        AdminAlerts().Should().Be(0);
    }

    [Fact]
    public async Task Preview_Sequence_ResponseUnchanged()
    {
        var r = await Scenario3();
        As("buyer1");
        var po = await NewPo(r, price: 150000);
        var res = await Send(new PreviewRecordApprovalRequest(po.Id, r.Confirmed));
        res.IsSuccess.Should().BeTrue(Dump(res));
        var p = (ApprovalPreviewResponse)res.Data!;

        p.Path.Should().BeNull();
        p.Stages.Should().OnlyContain(s => s.AnyGroup == null);
        // WebAPI 的序列化設定：null 不寫出，sequence 的 JSON 與第一版逐字相同
        var json = JsonSerializer.Serialize(p, new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        json.Should().NotContain("path").And.NotContain("anyGroup");
    }
}
