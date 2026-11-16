using System.Text.Json;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.ApplicationTests.Dynamic.Approvals.ApprovalGraphTestKit;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>發布（fg3a Task 4，J67）：版本規則、版面寫入、警告、樂觀鎖。</summary>
public class ApprovalFlowPublishTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private Func<Task>? _race;

    protected override void ConfigureServices(IServiceCollection services) => RacingFlowRepository.Register(services, () => _race);

    private static ApprovalFlowDraftInput Input(string key, long to, int priority, ApprovalDefinition d, JsonElement? layout = null,
        string? baseRev = null, long? baseVersionId = null, bool force = false) =>
        new(key, Text(key), to, priority, d, layout, baseRev, baseVersionId, force);

    private static ApprovalDefinition Linear(string approver = "fin1") =>
        G([Start(), Approval("a", Users(approver)), End()], [E("e0", "start", "a"), E("e1", "a", "end")]);

    private static JsonElement Layout(params (string Id, int X, int Y)[] nodes) =>
        JsonSerializer.SerializeToElement(new { nodes = nodes.ToDictionary(n => n.Id, n => new { x = n.X, y = n.Y }) });

    private async Task<PurchaseRig> Rig()
    {
        var r = await BuildPurchase();
        await GrantEveryone(r.Module);
        As("admin");
        return r;
    }

    private async Task<ApprovalFlowPublishResponse> Publish(string key, ApprovalFlowDraftInput input)
    {
        var res = await Send(new PublishApprovalFlowRequest(key, input));
        res.IsSuccess.Should().BeTrue(Dump(res));
        return (ApprovalFlowPublishResponse)res.Data!;
    }

    private List<ApprovalFlowVersion> Versions(string key)
    {
        var id = Flow(key).Id;
        return Db.Context.Set<ApprovalFlowVersion>().AsNoTracking().Where(v => v.FlowId == id).OrderBy(v => v.Version).ToList();
    }

    [Fact]
    public async Task Publish_DraftOnlyFlow_CreatesVersion1_StaysInactive_DraftCleared()
    {
        var r = await Rig();
        var created = (ApprovalFlowResponse)(await Send(new CreateApprovalFlowDraftRequest(r.Module, Input("po-g", r.Confirmed, 1, Linear())))).Data!;

        var got = await Publish("po-g", Input("po-g", r.Confirmed, 1, Linear("fin2"), Layout(("a", 100, 50)), baseRev: created.DraftRev));

        got.Flow.Version.Should().Be(1);
        got.Flow.Active.Should().BeFalse();
        got.Flow.Draft.Should().BeNull();
        var row = Flow("po-g");
        (row.DraftJson, row.DraftUpdatedBy, row.DraftUpdatedTime, row.Active).Should().Be(((string?)null, (string?)null, (DateTime?)null, false));
        row.CurrentVersionId.Should().Be(Versions("po-g").Single().Id);
        Versions("po-g").Single().Definition.Nodes!.Single(n => n.Id == "a").Approvers!.UserIds.Should().Equal("fin2");
        got.Flow.Layout!.Nodes["a"].Should().Be(new LayoutPoint(100, 50));
    }

    [Fact]
    public async Task Publish_UnusedVersion_OverwritesDefinitionAndLayout()
    {
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear());
        var v1 = Flow("po-g").CurrentVersionId;

        var got = await Publish("po-g", Input("po-g", r.Confirmed, 1, Linear("fin2"), Layout(("a", 10, 20)), baseVersionId: v1));

        got.Flow.Version.Should().Be(1);
        Versions("po-g").Should().ContainSingle().Which.Definition.Nodes!.Single(n => n.Id == "a").Approvers!.UserIds.Should().Equal("fin2");
        Versions("po-g").Single().LayoutJson.Should().Contain("\"a\"");
        Flow("po-g").CurrentVersionId.Should().Be(v1);
    }

    [Fact]
    public async Task Publish_UsedVersion_DefinitionChanged_NewVersionWithLayout()
    {
        var r = await Rig();
        var flow = await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear());
        await SeedInstance(r, flow);
        var v1 = Flow("po-g").CurrentVersionId;

        var got = await Publish("po-g", Input("po-g", r.Confirmed, 1, Linear("fin2"), Layout(("a", 10, 20)), baseVersionId: v1));

        (got.Flow.Version, got.Flow.PendingCount).Should().Be((2, 1));
        Versions("po-g").Select(v => v.Version).Should().Equal(1, 2);
        Versions("po-g")[0].Definition.Nodes!.Single(n => n.Id == "a").Approvers!.UserIds.Should().Equal(["fin1"], "舊版不動");
        Versions("po-g")[1].LayoutJson.Should().Contain("\"a\"");
        Flow("po-g").CurrentVersionId.Should().Be(Versions("po-g")[1].Id);
    }

    [Fact]
    public async Task Publish_UsedVersion_LayoutOnly_RewritesLayoutInPlace_NoNewVersion()
    {
        var r = await Rig();
        var flow = await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear());
        await SeedInstance(r, flow);
        var v1 = Flow("po-g").CurrentVersionId;

        var got = await Publish("po-g", Input("po-g", r.Confirmed, 1, Linear(), Layout(("a", 77, 88)), baseVersionId: v1));

        got.Flow.Version.Should().Be(1);
        Versions("po-g").Should().ContainSingle().Which.LayoutJson.Should().Contain("77");
        Flow("po-g").CurrentVersionId.Should().Be(v1);
    }

    [Fact]
    public async Task Publish_SequenceFlowConverted_BecomesGraph()
    {
        var r = await Rig();
        var seq = Def(null, Stage("lead", Group(PurchaseLeadGroup)));
        var flow = await SeedFlow(r, "po-a", r.Confirmed, 1, seq);
        await SeedInstance(r, flow);
        var v1 = Flow("po-a").CurrentVersionId;
        var graph = ApprovalGraphConversion.FromSequence(seq);
        var dry = (ApprovalFlowValidation)(await Send(new ValidateApprovalFlowRequest(r.Module, "po-a", Input("po-a", r.Confirmed, 1, graph)))).Data!;

        var got = await Publish("po-a", Input("po-a", r.Confirmed, 1, graph, baseVersionId: v1));

        dry.NewVersion.Should().BeTrue();
        got.Flow.Version.Should().Be(dry.NextVersion);
        got.Flow.Definition!.Shape.Should().Be(ApprovalShapes.Graph);
        Versions("po-a")[0].Definition.Shape.Should().Be(ApprovalShapes.Sequence);
    }

    [Fact]
    public async Task Publish_Invalid_400_NothingWritten_DraftKept()
    {
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear());
        var v1 = Flow("po-g").CurrentVersionId;
        var saved = (ApprovalFlowResponse)(await Send(new SaveApprovalFlowDraftRequest("po-g", Input("po-g", r.Confirmed, 1, Linear("fin2"), baseVersionId: v1)))).Data!;
        var before = Flow("po-g");
        var broken = G([Start(), Condition("c"), End()], [E("e0", "start", "c")]);

        var res = await Send(new PublishApprovalFlowRequest("po-g", Input("po-g", r.Confirmed, 1, broken, baseRev: saved.DraftRev, baseVersionId: v1)));

        res.IsInvalid.Should().BeTrue(Dump(res));
        Flow("po-g").Should().BeEquivalentTo(before);
        Versions("po-g").Should().ContainSingle();
        Versions("po-g").Single().Definition.Nodes!.Should().HaveCount(3);
        Versions("po-g").Single().Definition.Nodes!.Select(n => n.Id).Should().Equal("start", "a", "end");
    }

    [Fact]
    public async Task Publish_WithWarnings_Succeeds_ReturnsWarnings()
    {
        var r = await Rig();
        await SeedUnpublishedFlow(r, "po-g", r.Confirmed, 1);
        var def = G([Start(), Approval("a", Users("fin1")), Condition("c"), Approval("b", Users("fin2")), End()],
            [E("e0", "start", "a"), E("e1", "a", "c"), E("e2", "c", "b", Cond(r.Total, "gte", 1), order: 1), E("e3", "c", "end", isDefault: true), E("e4", "b", "end")]);

        var got = await Publish("po-g", Input("po-g", r.Confirmed, 1, def));

        got.Warnings.Should().ContainSingle().Which.Code.Should().Be("W2");
        got.Flow.Version.Should().Be(1);
    }

    [Fact]
    public async Task Publish_StaleRev_Conflict409()
    {
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear());
        var v1 = Flow("po-g").CurrentVersionId;
        var first = (ApprovalFlowResponse)(await Send(new SaveApprovalFlowDraftRequest("po-g", Input("po-g", r.Confirmed, 1, Linear("fin2"), baseVersionId: v1)))).Data!;
        await Send(new SaveApprovalFlowDraftRequest("po-g", Input("po-g", r.Confirmed, 1, Linear("emp1"), baseRev: first.DraftRev, baseVersionId: v1)));
        var before = Flow("po-g");

        var res = await Send(new PublishApprovalFlowRequest("po-g", Input("po-g", r.Confirmed, 1, Linear("emp2"), baseRev: first.DraftRev, baseVersionId: v1)));

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Code.Should().Be("DraftConflict");
        Flow("po-g").Should().BeEquivalentTo(before);
        Versions("po-g").Single().Definition.Nodes!.Single(n => n.Id == "a").Approvers!.UserIds.Should().Equal("fin1");
    }

    [Fact]
    public async Task Publish_DraftChangedAfterRead_NothingWritten()
    {
        // 驗證通過之後、清草稿之前，別人把草稿改掉：條件更新 0 列 → 409，沒有新版本、CurrentVersionId 不變
        var r = await Rig();
        var flow = await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear());
        await SeedInstance(r, flow);
        var v1 = Flow("po-g").CurrentVersionId;
        await Send(new SaveApprovalFlowDraftRequest("po-g", Input("po-g", r.Confirmed, 1, Linear("fin2"), baseVersionId: v1)));
        var before = Flow("po-g");
        var rev = ApprovalFlowDraft.TryParse(before.DraftJson)!.Rev;
        _race = () => Db.Context.Set<ApprovalFlow>().Where(f => f.Key == "po-g")
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.DraftJson, """{"rev":"someone-else","definition":{"shape":"graph"}}""").SetProperty(f => f.DraftUpdatedBy, "fin1"));

        var res = await Send(new PublishApprovalFlowRequest("po-g", Input("po-g", r.Confirmed, 1, Linear("emp1"), baseRev: rev, baseVersionId: v1)));

        res.IsConflict.Should().BeTrue(Dump(res));
        var info = (DraftConflictInfo)res.Data!;
        (info.DraftUpdatedBy, info.DraftRev).Should().Be(("fin1", "someone-else"));
        Versions("po-g").Should().ContainSingle();
        Flow("po-g").CurrentVersionId.Should().Be(v1);
        Flow("po-g").DraftJson.Should().Be(before.DraftJson, "失敗的請求整筆回滾（連同競爭者的寫入，它在同一個測試交易內）");
    }

    [Fact]
    public async Task SaveDraft_DraftChangedAfterRead_Conflict_NothingWritten_ViaHandler()
    {
        var r = await Rig();
        var created = (ApprovalFlowResponse)(await Send(new CreateApprovalFlowDraftRequest(r.Module, Input("po-g", r.Confirmed, 1, Linear())))).Data!;
        _race = () => Db.Context.Set<ApprovalFlow>().Where(f => f.Key == "po-g")
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.DraftJson, """{"rev":"someone-else","definition":{"shape":"graph"}}"""));

        var res = await Send(new SaveApprovalFlowDraftRequest("po-g",
            new("po-g", Text("改名"), r.Confirmed, 1, Linear("fin2"), null, created.DraftRev)));

        res.IsConflict.Should().BeTrue(Dump(res));
        Flow("po-g").NameI18n.Resolve("zh-TW").Should().Be("po-g", "名稱不寫");
    }

    [Fact]
    public async Task Publish_PrunesLayoutOfRemovedNodes()
    {
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear());
        var v1 = Flow("po-g").CurrentVersionId;

        var got = await Publish("po-g", Input("po-g", r.Confirmed, 1, Linear(), Layout(("a", 1, 2), ("ghost", 9, 9)), baseVersionId: v1));

        got.Flow.Layout!.Nodes.Keys.Should().Equal("a");
        Versions("po-g").Single().LayoutJson.Should().NotContain("ghost");
    }

    [Fact]
    public async Task Publish_ThenActivate_Allowed()
    {
        var r = await Rig();
        await SeedUnpublishedFlow(r, "po-g", r.Confirmed, 1);
        await Publish("po-g", Input("po-g", r.Confirmed, 1, Linear()));

        var res = await Send(new UpdateApprovalFlowRequest("po-g", new("po-g", Text("po-g"), r.Confirmed, 1, true, Linear())));

        res.IsSuccess.Should().BeTrue(Dump(res));
        Flow("po-g").Active.Should().BeTrue();
    }

    [Fact]
    public async Task Publish_AppliesNameAndKeepsActive()
    {
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear(), active: true);
        var v1 = Flow("po-g").CurrentVersionId;

        var got = await Publish("po-g", new("po-g", Text("新名稱"), r.Confirmed, 1, Linear("fin2"), null, null, v1));

        got.Flow.Name.Resolve("zh-TW").Should().Be("新名稱");
        got.Flow.Active.Should().BeTrue();
    }

    [Fact]
    public async Task Publish_ReadOnlyDesigner_Forbidden()
    {
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear());
        Db.Context.Add(new UserProgramPrivilege { UserId = "emp1", ProgramCode = "DYN00000", CanRead = true });
        await Db.Context.SaveChangesAsync();
        As("emp1");

        (await Send(new PublishApprovalFlowRequest("po-g", Input("po-g", r.Confirmed, 1, Linear("fin2"))))).IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task Update_NewVersion_CarriesLayoutPruned()
    {
        // 審查 🟡11：PUT 開新版本時沿用目前版本的版面並丟掉已不存在的節點，API 改定義不會讓版面退回自動排版
        var r = await Rig();
        var flow = await SeedGraphFlow(r, "po-g", r.Confirmed, 1, G(
            [Start(), Approval("a", Users("fin1")), Approval("b", Users("fin2")), End()],
            [E("e0", "start", "a"), E("e1", "a", "b"), E("e2", "b", "end")]));
        await Db.Context.Set<ApprovalFlowVersion>().Where(v => v.FlowId == flow.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.LayoutJson, """{"nodes":{"a":{"x":1,"y":2},"b":{"x":3,"y":4}}}"""));
        await SeedInstance(r, flow);

        var res = await Send(new UpdateApprovalFlowRequest("po-g", new("po-g", Text("po-g"), r.Confirmed, 1, true, Linear())));

        res.IsSuccess.Should().BeTrue(Dump(res));
        Versions("po-g").Select(v => v.Version).Should().Equal(1, 2);
        Versions("po-g")[1].LayoutJson.Should().Contain("\"a\"").And.NotContain("\"b\"");
        Versions("po-g")[0].LayoutJson.Should().Contain("\"b\"");
    }
}
