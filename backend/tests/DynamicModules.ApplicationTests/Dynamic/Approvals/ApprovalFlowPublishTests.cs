using System.Text.Json;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.ApplicationTests.Dynamic.Approvals.ApprovalGraphTestKit;
using DynamicModules.TestSupport;

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
        (row.DraftUpdatedBy, row.DraftUpdatedTime, row.Active).Should().Be(((string?)null, (DateTime?)null, false));
        // 草稿內容清掉，但留下一個新權杖（審查 🔴1）
        ApprovalFlowDraft.TryParse(row.DraftJson).Should().BeNull();
        ApprovalFlowDraft.TokenOf(row.DraftJson).Should().NotBeNullOrEmpty().And.NotBe(created.DraftRev);
        got.Flow.DraftRev.Should().Be(ApprovalFlowDraft.TokenOf(row.DraftJson));
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
        var info = (DraftConflictInfo)res.Params!;
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
        await Db.Context.SaveForTestsAsync();
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

    // ───────── 審查 🔴1：原地發布換權杖（ABA）─────────

    [Fact]
    public async Task Publish_InPlace_StaleBase_Conflicts()
    {
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear());
        var v1 = Flow("po-g").CurrentVersionId;
        // A、B 同時打開（沒有草稿，BaseRev = null、BaseVersionId = v1）；A 先發布（版本沒被用過，原地覆寫）
        var a = await Publish("po-g", Input("po-g", r.Confirmed, 1, Linear("fin2"), baseVersionId: v1));

        // B 以打開時的基準發布：要 409，不能覆蓋 A 剛發布的內容
        var res = await Send(new PublishApprovalFlowRequest("po-g", Input("po-g", r.Confirmed, 1, Linear("emp1"), baseVersionId: v1)));

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Code.Should().Be("DraftConflict");
        a.Flow.VersionId.Should().Be(v1, "原地發布不開新版本，所以權杖要靠 rev 換新");
        Versions("po-g").Single().Definition.Nodes!.Single(n => n.Id == "a").Approvers!.UserIds.Should().Equal("fin2");
    }

    [Fact]
    public async Task Publish_InPlace_NewTokenFromResponse_Succeeds()
    {
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear());
        var a = await Publish("po-g", Input("po-g", r.Confirmed, 1, Linear("fin2"), baseVersionId: Flow("po-g").CurrentVersionId));

        var b = await Publish("po-g", Input("po-g", r.Confirmed, 1, Linear("emp1"), baseRev: a.Flow.DraftRev, baseVersionId: a.Flow.VersionId));

        b.Flow.DraftRev.Should().NotBe(a.Flow.DraftRev);
        Versions("po-g").Single().Definition.Nodes!.Single(n => n.Id == "a").Approvers!.UserIds.Should().Equal("emp1");
    }

    [Fact]
    public async Task SaveDraft_AfterInPlacePublish_StaleBase_Conflicts()
    {
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear());
        var v1 = Flow("po-g").CurrentVersionId;
        await Publish("po-g", Input("po-g", r.Confirmed, 1, Linear("fin2"), baseVersionId: v1));

        // B 的草稿基準是 A 發布前的定義
        var res = await Send(new SaveApprovalFlowDraftRequest("po-g", Input("po-g", r.Confirmed, 1, Linear("emp1"), baseVersionId: v1)));

        res.IsConflict.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task Publish_Conflict_AfterSomeonePublished_ReportsLastModifier()
    {
        // 草稿已被發布清掉，409 的修改者與時間改報流程最後的修改者，前端才說得出是誰發布的（審查 🟢3）
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear());
        var v1 = Flow("po-g").CurrentVersionId;
        await Publish("po-g", Input("po-g", r.Confirmed, 1, Linear("fin2"), baseVersionId: v1));

        var res = await Send(new PublishApprovalFlowRequest("po-g", Input("po-g", r.Confirmed, 1, Linear("emp1"), baseVersionId: v1)));

        var info = (DraftConflictInfo)res.Params!;
        info.DraftUpdatedBy.Should().Be("admin");
        info.DraftUpdatedByName.Should().NotBeNullOrEmpty();
        info.DraftUpdatedTime.Should().Be(Flow("po-g").ModifiedTime);
        info.DraftRev.Should().Be(ApprovalFlowDraft.TokenOf(Flow("po-g").DraftJson));
    }

    [Fact]
    public async Task Publish_WithoutLayout_KeepsCurrentLayout()
    {
        // 請求沒帶版面：沿用目前版本的版面並 Prune（與 PUT 一致，審查 🟢2）
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear());
        var first = await Publish("po-g", Input("po-g", r.Confirmed, 1, Linear(), Layout(("a", 7, 8)), baseVersionId: Flow("po-g").CurrentVersionId));

        var got = await Publish("po-g", Input("po-g", r.Confirmed, 1, Linear("fin2"), baseRev: first.Flow.DraftRev, baseVersionId: first.Flow.VersionId));

        got.Flow.Layout!.Nodes["a"].Should().Be(new LayoutPoint(7, 8));
    }

    [Fact]
    public async Task Put_DefinitionChanged_InPlace_RotatesToken_ToggleDoesNot()
    {
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear(), active: false);
        var v1 = Flow("po-g").CurrentVersionId;
        var toOptionId = r.Confirmed;
        (await Send(new SaveApprovalFlowDraftRequest("po-g", Input("po-g", toOptionId, 1, Linear("emp1"), baseVersionId: v1)))).IsSuccess.Should().BeTrue();
        var draft = ApprovalFlowDraft.TryParse(Flow("po-g").DraftJson)!;

        // 只切啟停：權杖不變（卡片啟停不造成假衝突）
        (await Send(new UpdateApprovalFlowRequest("po-g", new("po-g", Text("po-g"), toOptionId, 1, true, Linear())))).IsSuccess.Should().BeTrue();
        ApprovalFlowDraft.TokenOf(Flow("po-g").DraftJson).Should().Be(draft.Rev);

        // 原地改定義（版本沒被用過、CurrentVersionId 不變）：換新權杖，草稿內容保留
        (await Send(new UpdateApprovalFlowRequest("po-g", new("po-g", Text("po-g"), toOptionId, 1, true, Linear("fin2"))))).IsSuccess.Should().BeTrue();
        Flow("po-g").CurrentVersionId.Should().Be(v1);
        var after = ApprovalFlowDraft.TryParse(Flow("po-g").DraftJson)!;
        after.Rev.Should().NotBe(draft.Rev);
        after.Definition.Should().BeEquivalentTo(draft.Definition);

        // 設計頁拿舊權杖存草稿：409
        var stale = await Send(new SaveApprovalFlowDraftRequest("po-g", Input("po-g", toOptionId, 1, Linear("emp2"), baseRev: draft.Rev, baseVersionId: v1)));
        stale.IsConflict.Should().BeTrue(Dump(stale));
    }

    [Fact]
    public async Task Put_DefinitionChanged_NoDraft_StaleBaseDraftSave_Conflicts()
    {
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear());
        var v1 = Flow("po-g").CurrentVersionId;

        (await Send(new UpdateApprovalFlowRequest("po-g", new("po-g", Text("po-g"), r.Confirmed, 1, true, Linear("fin2"))))).IsSuccess.Should().BeTrue();

        // 設計頁在 PUT 之前打開：基準是 (null, v1)
        var res = await Send(new SaveApprovalFlowDraftRequest("po-g", Input("po-g", r.Confirmed, 1, Linear("emp1"), baseVersionId: v1)));
        res.IsConflict.Should().BeTrue(Dump(res));
    }
}
