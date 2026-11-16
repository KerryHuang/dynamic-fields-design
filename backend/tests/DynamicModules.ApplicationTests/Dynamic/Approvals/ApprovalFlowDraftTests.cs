using System.Text.Json;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using static DynamicModules.ApplicationTests.Dynamic.Approvals.ApprovalGraphTestKit;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>設計器草稿的建立、儲存、捨棄與讀取（fg3a Task 3，J64、J65）：隨機權杖＋條件更新的樂觀鎖。</summary>
public class ApprovalFlowDraftTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private static ApprovalFlowDraftInput Input(string key, long to, int priority, ApprovalDefinition d, JsonElement? layout = null,
        string? baseRev = null, long? baseVersionId = null, bool force = false, string? name = null) =>
        new(key, Text(name ?? key), to, priority, d, layout, baseRev, baseVersionId, force);

    private static ApprovalDefinition Linear(string approver = "fin1") =>
        G([Start(), Approval("a", Users(approver)), End()], [E("e0", "start", "a"), E("e1", "a", "end")]);

    /// <summary>缺預設線、沒有簽核節點：存草稿不跑 §9，存得進去。</summary>
    private static ApprovalDefinition Unfinished() => G([Start(), Condition("c"), End()], [E("e0", "start", "c")]);

    private async Task<PurchaseRig> Rig()
    {
        var r = await BuildPurchase();
        await GrantEveryone(r.Module);
        As("admin");
        return r;
    }

    private async Task<ApprovalFlowResponse> Ok(Task<DynamicModules.Application.Common.ServiceResult> call, bool created = false)
    {
        var res = await call;
        res.IsSuccess.Should().BeTrue(Dump(res));
        res.IsCreated.Should().Be(created);
        return (ApprovalFlowResponse)res.Data!;
    }

    private Task<ApprovalFlowResponse> CreateDraft(PurchaseRig r, ApprovalFlowDraftInput input) =>
        Ok(Send(new CreateApprovalFlowDraftRequest(r.Module, input)), created: true);

    private Task<ApprovalFlowResponse> Save(string key, ApprovalFlowDraftInput input) => Ok(Send(new SaveApprovalFlowDraftRequest(key, input)));

    private static JsonElement Layout(object nodes) => JsonSerializer.SerializeToElement(new { nodes });

    private async Task SetDraftEditor(string key, string user)
    {
        await Db.Context.Set<ApprovalFlow>().Where(f => f.Key == key).ExecuteUpdateAsync(s => s.SetProperty(f => f.DraftUpdatedBy, user));
        Db.Context.ChangeTracker.Clear(); // ExecuteUpdate 不更新已追蹤的實體，測試共用同一個 context 時要清掉
    }

    // ───────── 建立 ─────────

    [Fact]
    public async Task CreateDraft_FlowWithoutVersion_ForcedInactive_DraftStored()
    {
        var r = await Rig();

        var got = await CreateDraft(r, Input("po-g", r.Confirmed, 1, Linear(), Layout(new { start = new { x = 0, y = 0 } })));

        got.Version.Should().BeNull();
        got.Definition.Should().BeNull();
        got.Active.Should().BeFalse();
        got.DraftRev.Should().NotBeNullOrEmpty();
        got.Draft!.Layout!.Nodes.Keys.Should().Equal("start");
        (got.DraftUpdatedBy, got.DraftUpdatedByName).Should().Be(("admin", "系統管理員"));
        var row = Flow("po-g");
        (row.CurrentVersionId, row.Active, row.Priority, row.ToOptionId).Should().Be(((long?)null, false, 1, r.Confirmed));
        ApprovalFlowDraft.TryParse(row.DraftJson)!.Rev.Should().Be(got.DraftRev);
        Db.Context.Set<ApprovalFlowVersion>().AsNoTracking().Count().Should().Be(0);
    }

    [Fact]
    public async Task CreateDraft_InvalidDefinitionStillSaved_NoRulesRun()
    {
        var r = await Rig();

        var got = await CreateDraft(r, Input("po-g", r.Confirmed, 1, Unfinished()));

        got.Draft!.Definition.Nodes.Should().HaveCount(3);
    }

    [Fact]
    public async Task CreateDraft_SequenceShape_Rejected()
    {
        var r = await Rig();

        var res = await Send(new CreateApprovalFlowDraftRequest(r.Module, Input("po-g", r.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup))))));

        res.Errors["definition.shape"].Should().Equal(ApprovalMessages.DraftShapeInvalid);
        Db.Context.Set<ApprovalFlow>().AsNoTracking().Count().Should().Be(0);
    }

    [Fact]
    public async Task CreateDraft_TooLarge_Rejected()
    {
        var r = await Rig();
        var big = G([new ApprovalNodeDef("start", ApprovalNodeKinds.Start, Text(new string('x', ApprovalGraphLimits.MaxDraftBytes))), End()], [E("e0", "start", "end")]);

        var res = await Send(new CreateApprovalFlowDraftRequest(r.Module, Input("po-g", r.Confirmed, 1, big)));

        res.Errors["definition"].Should().Equal(ApprovalMessages.DraftTooLarge);
    }

    [Fact]
    public async Task CreateDraft_LayoutOutOfRange_Rejected()
    {
        var r = await Rig();

        var res = await Send(new CreateApprovalFlowDraftRequest(r.Module, Input("po-g", r.Confirmed, 1, Linear(), Layout(new { start = new { x = 100001, y = 0 } }))));

        res.Errors["layout"].Should().Equal(ApprovalMessages.LayoutInvalid);
    }

    [Fact]
    public async Task CreateDraft_KeyOrder_Rejected()
    {
        var r = await Rig();

        var res = await Send(new CreateApprovalFlowDraftRequest(r.Module, Input("order", r.Confirmed, 1, Linear())));

        res.Errors["key"].Should().Equal(ApprovalMessages.FlowKeyReserved);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("validate")]
    [InlineData("new")]
    public async Task CreateDraft_KeysNamedLikeRoutes_AreOrdinaryFlows(string key)
    {
        var r = await Rig();

        var created = await CreateDraft(r, Input(key, r.Confirmed, 1, Linear()));
        var saved = await Save(key, Input(key, r.Confirmed, 1, Linear("fin2"), baseRev: created.DraftRev));
        var got = await Send(new GetApprovalFlowRequest(key));

        saved.DraftRev.Should().NotBe(created.DraftRev);
        ((ApprovalFlowResponse)got.Data!).Key.Should().Be(key);
    }

    [Fact]
    public async Task CreateDraft_KeyTaken_409_PriorityTaken_400()
    {
        var r = await Rig();
        await CreateDraft(r, Input("po-g", r.Confirmed, 1, Linear()));

        var taken = await Send(new CreateApprovalFlowDraftRequest(r.Module, Input("po-g", r.Confirmed, 2, Linear())));
        var dup = await Send(new CreateApprovalFlowDraftRequest(r.Module, Input("po-h", r.Confirmed, 1, Linear())));

        taken.IsConflict.Should().BeTrue(Dump(taken));
        dup.Errors.Keys.Should().Equal("priority");
    }

    // ───────── 儲存與樂觀鎖 ─────────

    [Fact]
    public async Task SaveDraft_NewRevToken_UpdatedByAndTimeSet()
    {
        var r = await Rig();
        var first = await CreateDraft(r, Input("po-g", r.Confirmed, 1, Linear()));

        var second = await Save("po-g", Input("po-g", r.Confirmed, 1, Linear("fin2"), baseRev: first.DraftRev));

        second.DraftRev.Should().NotBe(first.DraftRev);
        second.DraftUpdatedBy.Should().Be("admin");
        second.DraftUpdatedTime.Should().Be(new DateTime(2026, 11, 4, 9, 0, 0));
        Flow("po-g").DraftJson.Should().Contain("fin2");
    }

    [Fact]
    public async Task SaveDraft_StaleRev_Conflict409_WithCurrentEditorName()
    {
        var r = await Rig();
        var first = await CreateDraft(r, Input("po-g", r.Confirmed, 1, Linear()));
        await Save("po-g", Input("po-g", r.Confirmed, 1, Linear("fin2"), baseRev: first.DraftRev));
        await SetDraftEditor("po-g", "fin1");
        var before = Flow("po-g");

        var res = await Send(new SaveApprovalFlowDraftRequest("po-g", Input("po-g", r.Confirmed, 1, Linear("emp1"), baseRev: first.DraftRev)));

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Code.Should().Be("DraftConflict");
        var info = (DraftConflictInfo)res.Data!;
        (info.DraftUpdatedBy, info.DraftUpdatedByName, info.DraftUpdatedTime).Should().Be(("fin1", "fin1", before.DraftUpdatedTime));
        Flow("po-g").DraftJson.Should().Be(before.DraftJson);
    }

    [Fact]
    public async Task SaveDraft_StaleRev_Force_Overwrites()
    {
        var r = await Rig();
        var first = await CreateDraft(r, Input("po-g", r.Confirmed, 1, Linear()));
        await Save("po-g", Input("po-g", r.Confirmed, 1, Linear("fin2"), baseRev: first.DraftRev));

        var forced = await Save("po-g", Input("po-g", r.Confirmed, 1, Linear("emp1"), baseRev: first.DraftRev, force: true));

        forced.Draft!.Definition.Nodes!.Single(n => n.Id == "a").Approvers!.UserIds.Should().Equal("emp1");
    }

    [Fact]
    public async Task SaveDraft_VersionPublishedMeanwhile_Conflict409()
    {
        var r = await Rig();
        var flow = await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear());
        var v1 = flow.CurrentVersionId ?? Flow("po-g").CurrentVersionId;
        (await Send(new SaveApprovalFlowDraftRequest("po-g", Input("po-g", r.Confirmed, 1, Linear("fin2"), baseVersionId: v1)))).IsSuccess.Should().BeTrue();
        var v2 = new ApprovalFlowVersion { FlowId = flow.Id, Version = 2, Definition = Linear("emp1"), CreatedBy = "admin", CreatedTime = SeedTime };
        Db.Context.Add(v2);
        await Db.Context.SaveChangesAsync();
        await Db.Context.Set<ApprovalFlow>().Where(f => f.Id == flow.Id).ExecuteUpdateAsync(s => s.SetProperty(f => f.CurrentVersionId, v2.Id));
        var rev = Flow("po-g").DraftJson;

        var res = await Send(new SaveApprovalFlowDraftRequest("po-g", Input("po-g", r.Confirmed, 1, Linear("emp2"),
            baseRev: ApprovalFlowDraft.TryParse(rev)!.Rev, baseVersionId: v1)));

        res.IsConflict.Should().BeTrue(Dump(res));
        Flow("po-g").DraftJson.Should().Be(rev);
    }

    [Fact]
    public async Task SaveDraft_PublishedFlow_WithoutBaseVersion_Conflict409()
    {
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear());

        var res = await Send(new SaveApprovalFlowDraftRequest("po-g", Input("po-g", r.Confirmed, 1, Linear("fin2"))));

        res.IsConflict.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task SaveDraft_AfterDiscardAndResave_OldBaseConflicts()
    {
        // ABA：A 讀到 rev X → B 捨棄 → C 存新草稿 → A 以 X 存 → 409（隨機權杖不會撞回 X）
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear());
        var v1 = Flow("po-g").CurrentVersionId;
        var x = await Save("po-g", Input("po-g", r.Confirmed, 1, Linear("fin2"), baseVersionId: v1));
        (await Send(new DiscardApprovalFlowDraftRequest("po-g", x.DraftRev, false))).IsSuccess.Should().BeTrue();
        var y = await Save("po-g", Input("po-g", r.Confirmed, 1, Linear("emp1"), baseVersionId: v1));

        var res = await Send(new SaveApprovalFlowDraftRequest("po-g", Input("po-g", r.Confirmed, 1, Linear("emp2"), baseRev: x.DraftRev, baseVersionId: v1)));

        y.DraftRev.Should().NotBe(x.DraftRev);
        res.IsConflict.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task SaveDraft_DraftChangedAfterRead_Conflict_NothingWritten()
    {
        var r = await Rig();
        var first = await CreateDraft(r, Input("po-g", r.Confirmed, 1, Linear()));
        var stale = Flow("po-g").DraftJson;
        await Save("po-g", Input("po-g", r.Confirmed, 1, Linear("fin2"), baseRev: first.DraftRev));
        var current = Flow("po-g");
        var repo = Svc<IApprovalFlowRepository>();

        var lost = await repo.TryReplaceDraftAsync(current.Id, stale, null, """{"rev":"x"}""", "emp1", SeedTime, default);

        lost.Should().BeFalse("期望值是讀取當時的舊字串");
        Flow("po-g").Should().BeEquivalentTo(current);
        (await repo.TryReplaceDraftAsync(current.Id, current.DraftJson, current.CurrentVersionId, null, null, null, default)).Should().BeTrue();
        Flow("po-g").DraftJson.Should().BeNull();
    }

    [Fact]
    public async Task SaveDraft_MalformedExistingDraft_TreatedAsNone()
    {
        var r = await Rig();
        await SeedUnpublishedFlow(r, "po-g", r.Confirmed, 1, """{"shape":"graph"}""");

        var got = await Save("po-g", Input("po-g", r.Confirmed, 1, Linear()));

        got.DraftRev.Should().NotBeNullOrEmpty();
        got.Draft!.Definition.Nodes.Should().HaveCount(3);
    }

    [Fact]
    public async Task SaveDraft_RenamesFlowImmediately_DefinitionUnchangedUntilPublish()
    {
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear());
        var v1 = Flow("po-g").CurrentVersionId;

        var got = await Save("po-g", Input("po-g", r.Confirmed, 1, Linear("fin2"), baseVersionId: v1, name: "大額採購"));

        got.Name.Resolve("zh-TW").Should().Be("大額採購");
        Flow("po-g").NameI18n.Resolve("zh-TW").Should().Be("大額採購");
        got.Definition!.Nodes!.Single(n => n.Id == "a").Approvers!.UserIds.Should().Equal(["fin1"], "已發布的定義不變");
        got.Draft!.Definition.Nodes!.Single(n => n.Id == "a").Approvers!.UserIds.Should().Equal("fin2");
        got.Version.Should().Be(1);
    }

    [Fact]
    public async Task SaveDraft_UnchangedTarget_KeepsPriorityAndActive()
    {
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 3, Linear(), active: true);
        var v1 = Flow("po-g").CurrentVersionId;

        var got = await Save("po-g", Input("po-g", r.Confirmed, 99, Linear("fin2"), baseVersionId: v1));

        (got.Priority, got.Active).Should().Be((3, true));
    }

    [Fact]
    public async Task SaveDraft_UsedFlow_RetargetRejected()
    {
        var r = await Rig();
        var flow = await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear());
        await SeedInstance(r, flow);
        var before = Flow("po-g");

        var res = await Send(new SaveApprovalFlowDraftRequest("po-g", Input("po-g", r.Cancelled, 1, Linear("fin2"), baseVersionId: before.CurrentVersionId)));

        res.Errors["toOptionId"].Should().Equal(ApprovalFlowEditor.UsedCannotRetarget);
        Flow("po-g").Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task SaveDraft_PublishedFlow_RetargetRunsConstraints_400()
    {
        // 已確認需簽核，唯一的流程搬走後它就沒有流程了：入口約束照已發布的定義重算，名稱、目標狀態、草稿都不寫
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear());
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        var before = Flow("po-g");

        var res = await Send(new SaveApprovalFlowDraftRequest("po-g",
            Input("po-g", r.Cancelled, 1, Linear("fin2"), baseVersionId: before.CurrentVersionId, name: "改名")));

        res.IsInvalid.Should().BeTrue(Dump(res));
        res.Errors.Keys.Should().Contain($"states[{r.Confirmed}]");
        Flow("po-g").Should().BeEquivalentTo(before);
        Flow("po-g").DraftJson.Should().BeNull();
    }

    [Fact]
    public async Task SaveDraft_PublishedFlow_RetargetValid_Applied()
    {
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear());
        var v1 = Flow("po-g").CurrentVersionId;

        var got = await Save("po-g", Input("po-g", r.Cancelled, 4, Linear("fin2"), baseVersionId: v1, name: "取消用"));

        (got.ToOptionId, got.Priority).Should().Be((r.Cancelled, 4));
        got.Name.Resolve("zh-TW").Should().Be("取消用");
        got.DraftRev.Should().NotBeNull();
    }

    [Fact]
    public async Task SaveDraft_DraftOnlyFlow_RetargetSkipsConstraints()
    {
        var r = await Rig();
        await SeedGraphFlow(r, "po-a", r.Confirmed, 1, Linear());
        await SeedUnpublishedFlow(r, "po-u", r.Confirmed, 2);
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();

        var got = await Save("po-u", Input("po-u", r.Cancelled, 1, Unfinished()));

        got.ToOptionId.Should().Be(r.Cancelled);
    }

    [Fact]
    public async Task SaveDraft_DoesNotAffectSubmitOrPreview()
    {
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, G([Start(), Approval("a", Users("gm")), End()], [E("e0", "start", "a"), E("e1", "a", "end")]));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await Save("po-g", Input("po-g", r.Confirmed, 1, Linear("emp1"), baseVersionId: Flow("po-g").CurrentVersionId));
        As("buyer1");
        var po = await NewPo(r, price: 5000);

        var preview = await Send(new PreviewRecordApprovalRequest(po.Id, r.Confirmed));
        var submit = await MustSubmit(po.Id, r.Confirmed);

        ((ApprovalPreviewResponse)preview.Data!).Stages.Single().Approvers.Select(u => u.UserId).Should().Equal("gm");
        Inst(submit.InstanceId!.Value).FlowVersionId.Should().Be(Flow("po-g").CurrentVersionId);
        Db.Context.Set<ApprovalTask>().AsNoTracking().Select(t => t.AssigneeUserId).Should().Equal("gm");
    }

    // ───────── 捨棄 ─────────

    [Fact]
    public async Task DiscardDraft_ClearsDraft_KeepsVersion()
    {
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear());
        var saved = await Save("po-g", Input("po-g", r.Confirmed, 1, Linear("fin2"), baseVersionId: Flow("po-g").CurrentVersionId));

        var got = await Ok(Send(new DiscardApprovalFlowDraftRequest("po-g", saved.DraftRev, false)));

        (got.Draft, got.DraftRev, got.DraftUpdatedBy, got.DraftUpdatedTime).Should().Be(((ApprovalFlowDraftView?)null, (string?)null, (string?)null, (DateTime?)null));
        got.Version.Should().Be(1);
        var row = Flow("po-g");
        (row.DraftJson, row.DraftUpdatedBy, row.DraftUpdatedTime).Should().Be(((string?)null, (string?)null, (DateTime?)null));
    }

    [Fact]
    public async Task DiscardDraft_StaleRev_Conflict409()
    {
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear());
        await Save("po-g", Input("po-g", r.Confirmed, 1, Linear("fin2"), baseVersionId: Flow("po-g").CurrentVersionId));

        var res = await Send(new DiscardApprovalFlowDraftRequest("po-g", "old-rev", false));

        res.IsConflict.Should().BeTrue(Dump(res));
        Flow("po-g").DraftJson.Should().NotBeNull();
    }

    [Fact]
    public async Task DiscardDraft_StaleRev_Force_Discards()
    {
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear());
        await Save("po-g", Input("po-g", r.Confirmed, 1, Linear("fin2"), baseVersionId: Flow("po-g").CurrentVersionId));

        var got = await Ok(Send(new DiscardApprovalFlowDraftRequest("po-g", "old-rev", true)));

        got.Draft.Should().BeNull();
        Flow("po-g").DraftJson.Should().BeNull();
    }

    [Fact]
    public async Task DiscardDraft_DraftOnlyFlow_Conflict()
    {
        var r = await Rig();
        var created = await CreateDraft(r, Input("po-g", r.Confirmed, 1, Linear()));

        var res = await Send(new DiscardApprovalFlowDraftRequest("po-g", created.DraftRev, false));

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Code.Should().Be("DraftOnlyUseDelete");
        Flow("po-g").DraftJson.Should().NotBeNull();
    }

    // ───────── 讀取 ─────────

    [Fact]
    public async Task Get_SequenceFlow_ReturnsConvertedGraph()
    {
        var r = await Rig();
        var seq = Def(null, Stage("lead", Group(PurchaseLeadGroup)));
        await SeedFlow(r, "po-a", r.Confirmed, 1, seq);

        var got = (ApprovalFlowResponse)(await Send(new GetApprovalFlowRequest("po-a"))).Data!;

        got.Definition!.Shape.Should().Be(ApprovalShapes.Sequence);
        ApprovalDefinitionJson.SameAs(got.Graph!, ApprovalGraphConversion.FromSequence(seq)).Should().BeTrue();
    }

    [Fact]
    public async Task Get_GraphFlow_NoGraphField()
    {
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear());

        var got = (ApprovalFlowResponse)(await Send(new GetApprovalFlowRequest("po-g"))).Data!;

        got.Graph.Should().BeNull();
        got.Definition!.Shape.Should().Be(ApprovalShapes.Graph);
    }

    [Fact]
    public async Task Get_ReturnsLayoutAndDraftMeta()
    {
        var r = await Rig();
        var flow = await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear());
        await Db.Context.Set<ApprovalFlowVersion>().Where(v => v.FlowId == flow.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.LayoutJson, """{"nodes":{"a":{"x":120,"y":40}}}"""));
        await Save("po-g", Input("po-g", r.Confirmed, 1, Linear("fin2"), baseVersionId: Flow("po-g").CurrentVersionId));
        await SetDraftEditor("po-g", "fin1");

        var got = (ApprovalFlowResponse)(await Send(new GetApprovalFlowRequest("po-g"))).Data!;

        got.Layout!.Nodes["a"].Should().Be(new LayoutPoint(120, 40));
        got.VersionId.Should().Be(Flow("po-g").CurrentVersionId);
        (got.DraftUpdatedBy, got.DraftUpdatedByName, got.DraftUpdatedTime).Should().Be(("fin1", "fin1", new DateTime(2026, 11, 4, 9, 0, 0)));
        got.DraftRev.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task List_OmitsGraph()
    {
        var r = await Rig();
        await SeedFlow(r, "po-a", r.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup))));

        var list = (IReadOnlyList<ApprovalFlowResponse>)(await Send(new GetApprovalFlowsRequest(r.Module))).Data!;

        list.Single().Graph.Should().BeNull();
    }

    [Fact]
    public async Task List_MalformedDraft_DoesNotBreakList()
    {
        var r = await Rig();
        await SeedGraphFlow(r, "po-a", r.Confirmed, 1, Linear());
        await SeedUnpublishedFlow(r, "po-broken", r.Confirmed, 2, "{not json");
        await SeedUnpublishedFlow(r, "po-old", r.Confirmed, 3, """{"shape":"graph"}""");

        var res = await Send(new GetApprovalFlowsRequest(r.Module));

        res.IsSuccess.Should().BeTrue(Dump(res));
        var list = (IReadOnlyList<ApprovalFlowResponse>)res.Data!;
        list.Select(f => f.Key).Should().Equal("po-a", "po-broken", "po-old");
        list.Where(f => f.Key != "po-a").Should().OnlyContain(f => f.Draft == null && f.DraftRev == null);
    }

    [Fact]
    public async Task Get_MalformedDraft_DoesNotThrow()
    {
        var r = await Rig();
        await SeedUnpublishedFlow(r, "po-broken", r.Confirmed, 1, "{not json");

        var res = await Send(new GetApprovalFlowRequest("po-broken"));

        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task DraftEndpoints_ReadOnlyDesigner_Forbidden()
    {
        var r = await Rig();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear());
        Db.Context.Add(new UserProgramPrivilege { UserId = "emp1", ProgramCode = "DYN00000", CanRead = true });
        await Db.Context.SaveChangesAsync();
        As("emp1");

        (await Send(new SaveApprovalFlowDraftRequest("po-g", Input("po-g", r.Confirmed, 1, Linear("fin2"))))).IsForbidden.Should().BeTrue();
        (await Send(new CreateApprovalFlowDraftRequest(r.Module, Input("po-h", r.Confirmed, 2, Linear())))).IsForbidden.Should().BeTrue();
        (await Send(new DiscardApprovalFlowDraftRequest("po-g", null, true))).IsForbidden.Should().BeTrue();
    }
}
