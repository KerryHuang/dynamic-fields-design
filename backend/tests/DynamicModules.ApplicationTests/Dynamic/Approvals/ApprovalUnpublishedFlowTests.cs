using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>
/// 只有草稿的流程（流程圖設計 §10.8）：流程列沒有已發布版本時，清單與回應不崩、PUT 回 409、
/// 比對、約束、快照、試算、匯出都不把它算進去，優先序檢查要看得到它。
/// </summary>
public class ApprovalUnpublishedFlowTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private const string DraftBody = """{"rev":"seed-rev","definition":{"shape":"graph","nodes":[],"edges":[]},"layout":null}""";

    private static ApprovalFlowInput Input(string key, long to, int priority, ApprovalDefinition d, bool active = true) =>
        new(key, Text(key), to, priority, active, d);

    /// <summary>已發布的 po-a（總額 ≥ 100000）、只有草稿而且啟用中的 po-u（無條件，優先序最前），草稿 → 已確認 需簽核。</summary>
    private async Task<PurchaseRig> WithUnpublishedFirst(string noMatch)
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-a", r.Confirmed, 2, Def(Cond(r.Total, "gte", 100000), Stage("lead", Group(PurchaseLeadGroup))));
        await SeedUnpublishedFlow(r, "po-u", r.Confirmed, 1, DraftBody, active: true);
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed, noMatch), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module);
        return r;
    }

    [Fact]
    public async Task List_IncludesUnpublishedFlow_VersionAndDefinitionNull()
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-a", r.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup))));
        await SeedUnpublishedFlow(r, "po-u", r.Confirmed, 2, DraftBody);
        await SeedUnpublishedFlow(r, "po-n", r.Confirmed, 3);

        var res = await Send(new GetApprovalFlowsRequest(r.Module));

        res.IsSuccess.Should().BeTrue(Dump(res));
        var list = (IReadOnlyList<ApprovalFlowResponse>)res.Data!;
        list.Select(f => f.Key).Should().Equal("po-a", "po-u", "po-n");
        var u = list.Single(f => f.Key == "po-u");
        (u.Version, u.Definition, u.Used, u.PendingCount, u.CurrentVersionUsed).Should().Be(((int?)null, (ApprovalDefinition?)null, false, 0, false));
        u.Draft!.Definition.Shape.Should().Be(ApprovalShapes.Graph);
        u.Draft.Layout.Should().BeNull();
        u.DraftRev.Should().Be("seed-rev");
        list.Single(f => f.Key == "po-n").Draft.Should().BeNull();
        list.Single(f => f.Key == "po-a").Version.Should().Be(1);
    }

    [Fact]
    public async Task Get_UnpublishedFlow_200_NoCrash()
    {
        var r = await BuildPurchase();
        await SeedUnpublishedFlow(r, "po-u", r.Confirmed, 1, DraftBody);

        var res = await Send(new GetApprovalFlowRequest("po-u"));

        res.IsSuccess.Should().BeTrue(Dump(res));
        ((ApprovalFlowResponse)res.Data!).Version.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Update_UnpublishedFlow_NothingWritten(bool active)
    {
        var r = await BuildPurchase();
        await SeedUnpublishedFlow(r, "po-u", r.Confirmed, 1, DraftBody, active: !active);
        var before = Flow("po-u");

        var res = await Send(new UpdateApprovalFlowRequest("po-u",
            Input("po-u", r.Confirmed, 5, Def(null, Stage("lead", Group(PurchaseLeadGroup))), active)));

        // 啟用是 400（先發布，§10.8），其餘維持 fg1 的 409
        if (active) res.Errors["active"].Should().Equal(ApprovalMessages.FlowNotPublished);
        else
        {
            res.IsConflict.Should().BeTrue(Dump(res));
            res.Message.Should().Be(ApprovalMessages.FlowNotPublished);
        }
        Flow("po-u").Should().BeEquivalentTo(before);
        Db.Context.Set<ApprovalFlowVersion>().AsNoTracking().Count().Should().Be(0);
    }

    [Fact]
    public async Task ListCurrentByModule_SkipsUnpublishedFlow_ListByModuleKeepsIt()
    {
        var r = await BuildPurchase();
        var a = await SeedFlow(r, "po-a", r.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup))));
        await SeedUnpublishedFlow(r, "po-u", r.Confirmed, 2, DraftBody, active: true);
        var repo = Svc<IApprovalFlowRepository>();

        var current = await repo.ListCurrentByModuleAsync(r.ModuleId, default);
        var all = await repo.ListByModuleAsync(r.ModuleId, default);

        current.Select(f => f.Flow.Key).Should().Equal("po-a");
        current[0].VersionId.Should().Be(Flow("po-a").CurrentVersionId);
        all.Select(x => (x.Flow.Key, HasVersion: x.Version is not null)).Should().Equal(("po-a", true), ("po-u", false));
        a.Id.Should().Be(current[0].Flow.Id);
    }

    [Fact]
    public async Task UnpublishedFlow_ActiveInDb_NotMatched_Direct()
    {
        // po-u 啟用而且無條件、優先序在前：若被拿來比對，這張 5000 的單據會被它收走建出實例
        var r = await WithUnpublishedFirst(ApprovalNoMatch.Direct);
        As("buyer1");
        var po = await NewPo(r, price: 5000);

        var res = await MustSubmit(po.Id, r.Confirmed, status: 200);

        res.Outcome.Should().Be(ApprovalOutcomes.Direct);
        StateOf(r, po.Id).Should().Be(r.Confirmed);
        Db.Context.Set<ApprovalInstance>().AsNoTracking().Count().Should().Be(0);
    }

    [Fact]
    public async Task UnpublishedFlow_ActiveInDb_NotMatched_Block()
    {
        var r = await WithUnpublishedFirst(ApprovalNoMatch.Block);
        As("buyer1");
        var po = await NewPo(r, price: 5000);

        var res = await Submit(po.Id, r.Confirmed);

        res.IsSuccess.Should().BeFalse();
        Dump(res).Should().Contain(ApprovalMessages.NoMatchingFlow.Resolve(null));
        Db.Context.Set<ApprovalInstance>().AsNoTracking().Count().Should().Be(0);
    }

    [Fact]
    public async Task Submit_SnapshotExcludesUnpublishedFlow()
    {
        var r = await WithUnpublishedFirst(ApprovalNoMatch.Direct);
        As("buyer1");
        var po = await NewPo(r, price: 150000);

        var res = await MustSubmit(po.Id, r.Confirmed);

        Inst(res.InstanceId!.Value).FlowSetSnapshot.Select(e => e.Priority).Should().Equal(2);
    }

    [Fact]
    public async Task UnpublishedFlow_NotCountedForNoFlowConstraint()
    {
        var r = await BuildPurchase();
        await SeedUnpublishedFlow(r, "po-u", r.Confirmed, 1, DraftBody, active: true);

        var res = await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed, ApprovalNoMatch.Direct), Plain(r.Confirmed, r.Partial),
            Plain(r.Partial, r.Received), Plain(r.Confirmed, r.Cancelled)]);

        res.IsSuccess.Should().BeFalse();
        res.Errors.Should().ContainKey($"states[{r.Confirmed}]").WhoseValue.Should().Contain(ApprovalMessages.NoFlow("已確認"));
    }

    [Fact]
    public async Task UnpublishedFlow_NotInDraftPreviewFlowSet()
    {
        var r = await WithUnpublishedFirst(ApprovalNoMatch.Block);
        As("admin");
        var draft = Input("po-new", r.Confirmed, 9, Def(null, Stage("lead", Group(PurchaseLeadGroup))));

        var res = await Send(new PreviewApprovalDraftRequest(r.Module, draft, null, null, "buyer1"));

        res.IsSuccess.Should().BeTrue(Dump(res));
        // po-a（≥ 100000）不成立，po-u 不在比對集合裡，所以命中草稿本身
        ((ApprovalPreviewResponse)res.Data!).FlowKey.Should().Be("po-new");
    }

    [Fact]
    public async Task Create_PriorityTakenByUnpublishedFlow_400()
    {
        var r = await BuildPurchase();
        await SeedUnpublishedFlow(r, "po-u", r.Confirmed, 1, DraftBody);

        var res = await Send(new CreateApprovalFlowRequest(r.Module,
            Input("po-new", r.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup))))));

        res.IsSuccess.Should().BeFalse();
        res.Errors.Should().ContainKey("priority");
        Db.Context.Set<ApprovalFlow>().AsNoTracking().Count(f => f.Key == "po-new").Should().Be(0);
    }

    [Fact]
    public async Task Reorder_IncludesUnpublishedFlow()
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-a", r.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup))));
        await SeedUnpublishedFlow(r, "po-u", r.Confirmed, 2, DraftBody);

        var res = await Send(new ReorderApprovalFlowsRequest(r.Module, r.Confirmed, ["po-u", "po-a"]));

        res.IsSuccess.Should().BeTrue(Dump(res));
        var list = (List<ApprovalFlowResponse>)res.Data!;
        list.Select(f => (f.Key, f.Priority, f.Version)).Should().Equal(("po-u", 1, (int?)null), ("po-a", 2, 1));
        (await Send(new ReorderApprovalFlowsRequest(r.Module, r.Confirmed, ["po-a"]))).IsSuccess.Should().BeFalse("必須列出草稿流程");
    }

    [Fact]
    public async Task Delete_UnpublishedFlow_Removed()
    {
        var r = await BuildPurchase();
        await SeedUnpublishedFlow(r, "po-u", r.Confirmed, 1, DraftBody);

        var res = await Send(new DeleteApprovalFlowRequest("po-u"));

        res.IsSuccess.Should().BeTrue(Dump(res));
        Db.Context.Set<ApprovalFlow>().AsNoTracking().Count(f => f.Key == "po-u").Should().Be(0);
    }

    [Fact]
    public async Task Lock_IgnoresUnpublishedFlow()
    {
        // 草稿寫了可編輯欄位與條件欄位：沒發布的內容不能把欄位鎖起來（U3）
        var r = await WithUnpublishedFirst(ApprovalNoMatch.Direct);
        var draft = JsonSerializer.Serialize(new
        {
            rev = "seed-rev", definition = new { shape = "graph", editable = new[] { r.Memo, r.Discount } }, layout = (object?)null
        });
        await Db.Context.Set<ApprovalFlow>().Where(f => f.Key == "po-u").ExecuteUpdateAsync(s => s.SetProperty(f => f.DraftJson, draft));
        using var scope = NewScope();
        var module = (await scope.ServiceProvider.GetRequiredService<IModuleRepository>().GetByKeyAsync(r.Module, includeChildren: true, default))!;

        var l = await scope.ServiceProvider.GetRequiredService<ApprovalLockService>().ForModuleAsync(module, null, default);

        l.FieldIds.Should().Contain(r.Total).And.NotContain([r.Memo, r.Discount]);
    }

    [Theory]
    [InlineData(150000, ApprovalPreviewOutcomes.Flow, "po-a")]
    [InlineData(5000, ApprovalPreviewOutcomes.Blocked, null)]
    public async Task PreviewSavedFlow_OfUnpublishedFlow_ComparesPublishedFlows_DraftNotRead(int price, string outcome, string? flowKey)
    {
        // 設計器對只有草稿的流程做「已存流程試算」：不讀 DraftJson、不當成候選，回報實際命中的已發布流程（沒命中時是 blocked）
        var r = await WithUnpublishedFirst(ApprovalNoMatch.Block);
        As("buyer1");
        var po = await NewPo(r, price: price);
        As("admin");

        var res = await Send(new PreviewApprovalFlowRequest("po-u", po.Id, null, "buyer1"));

        res.IsSuccess.Should().BeTrue(Dump(res));
        var body = (ApprovalPreviewResponse)res.Data!;
        (body.Outcome, body.FlowKey).Should().Be((outcome, flowKey));
    }
}
