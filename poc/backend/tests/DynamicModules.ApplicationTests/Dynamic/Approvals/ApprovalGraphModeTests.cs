using System.Text.Json;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.ApplicationTests.Dynamic.Approvals.ApprovalGraphTestKit;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>graph 執行期共用輔助（計畫 fg2 J2、J27）：graph 判斷、引擎支援的節點型別、條件求值轉接。</summary>
public class ApprovalGraphModeTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private static readonly DateOnly Today = new(2026, 11, 4);
    private static readonly IReadOnlySet<string> NoUsers = new HashSet<string>();

    private static ApprovalDefinition Linear() =>
        G([Start(), Approval("mgr", Manager(1)), End()], [E("e1", "start", "mgr"), E("e2", "mgr", "end")]);

    private static ApprovalDefinition SequenceDef() => Def(null, Stage("lead", Group(1)));

    private static JsonElement? V(object value) => JsonSerializer.SerializeToElement(value);

    // ───────── Applies ─────────

    [Fact]
    public void Applies_GraphFlow_True()
    {
        var inst = new ApprovalInstance { RouteKind = ApprovalRouteKind.Flow };
        ApprovalGraphMode.Applies(inst, Linear()).Should().BeTrue();
    }

    [Fact]
    public void Applies_FallbackInstance_False()
    {
        // 備援實例沿用舊的綁定版本（可能是 graph），仍走第一版的單一關或簽
        var inst = new ApprovalInstance { RouteKind = ApprovalRouteKind.Fallback };
        ApprovalGraphMode.Applies(inst, Linear()).Should().BeFalse();
    }

    [Fact]
    public void Applies_Sequence_False()
    {
        var inst = new ApprovalInstance { RouteKind = ApprovalRouteKind.Flow };
        ApprovalGraphMode.Applies(inst, SequenceDef()).Should().BeFalse();
    }

    // ───────── SupportedKinds ─────────

    [Fact]
    public void SupportedKinds_SixKinds_NoNotify()
    {
        ApprovalGraphMode.SupportedKinds.Should().BeEquivalentTo(
            [ApprovalNodeKinds.Start, ApprovalNodeKinds.End, ApprovalNodeKinds.Approval, ApprovalNodeKinds.Condition, ApprovalNodeKinds.Fork, ApprovalNodeKinds.Join]);
        ApprovalGraphMode.SupportedKinds.Should().NotContain(ApprovalNodeKinds.Notify);
    }

    [Fact]
    public void Build_WithSupportedKinds_NotifyRejected()
    {
        // 設計 §7.1 範例含通知節點：結構合法，但引擎還不支援，建圖回 null 與 unsupportedKind
        var def = G(
            [Start(), Approval("mgr", Manager(1)), Condition("amount"), Fork("split"), Approval("legal", Group(5)), Approval("fin", Group(6)),
             Join("merge", "split"), Notify("tell-hr"), End()],
            [E("e1", "start", "mgr"), E("e2", "mgr", "amount"), E("e3", "amount", "split", Cond(21, "gte", 100000), order: 1), E("e4", "amount", "tell-hr", isDefault: true),
             E("e5", "split", "legal"), E("e6", "split", "fin"), E("e7", "legal", "merge"), E("e8", "fin", "merge"), E("e9", "merge", "tell-hr"), E("e10", "tell-hr", "end")]);

        var g = ApprovalGraphRuntime.Build(def, ApprovalGraphMode.SupportedKinds, out var errors);

        g.Should().BeNull();
        errors.Should().ContainSingle().Which.Should().Be(new GraphIssue("nodes.tell-hr", "runtime", "unsupportedKind"));
    }

    [Fact]
    public void Build_WithSupportedKinds_AcceptsGraphWithoutNotify()
    {
        ApprovalGraphRuntime.Build(Linear(), ApprovalGraphMode.SupportedKinds, out var errors).Should().NotBeNull();
        errors.Should().BeEmpty();
    }

    // ───────── Eval ─────────

    private async Task<(PurchaseRig R, Module Module, FieldTypeRegistry Registry)> Setup()
    {
        var r = await BuildPurchase();
        using var scope = NewScope();
        var module = (await scope.ServiceProvider.GetRequiredService<IModuleRepository>().GetByKeyAsync(r.Module, includeChildren: true, CancellationToken.None))!;
        return (r, module, scope.ServiceProvider.GetRequiredService<FieldTypeRegistry>());
    }

    private static Dictionary<string, JsonElement?> Values(PurchaseRig r, decimal total) => new() { [$"f_{r.Total}"] = V(total) };

    [Fact]
    public async Task Eval_Holds()
    {
        var (r, module, registry) = await Setup();
        var eval = ApprovalGraphMode.Eval(module, Values(r, 150000), registry, Today, NoUsers);
        eval(Cond(r.Total, "gte", 100000)).Should().Be(ConditionTruth.Holds);
    }

    [Fact]
    public async Task Eval_Fails()
    {
        var (r, module, registry) = await Setup();
        var eval = ApprovalGraphMode.Eval(module, Values(r, 5000), registry, Today, NoUsers);
        eval(Cond(r.Total, "gte", 100000)).Should().Be(ConditionTruth.Fails);
    }

    [Fact]
    public async Task Eval_UnknownField_Undecidable()
    {
        // 引用的欄位已不在（例如被刪除）
        var (r, module, registry) = await Setup();
        var eval = ApprovalGraphMode.Eval(module, Values(r, 150000), registry, Today, NoUsers);
        eval(Cond(987654, "gte", 1)).Should().Be(ConditionTruth.Undecidable);
    }

    [Fact]
    public async Task Eval_EmptyGroup_Holds()
    {
        var (r, module, registry) = await Setup();
        var eval = ApprovalGraphMode.Eval(module, Values(r, 5000), registry, Today, NoUsers);
        eval(JsonSerializer.SerializeToElement(new { all = Array.Empty<object>() })).Should().Be(ConditionTruth.Holds);
    }

    [Fact]
    public async Task Eval_NotObject_Undecidable()
    {
        var (r, module, registry) = await Setup();
        var eval = ApprovalGraphMode.Eval(module, Values(r, 5000), registry, Today, NoUsers);
        eval(JsonSerializer.SerializeToElement("gte")).Should().Be(ConditionTruth.Undecidable);
        eval(JsonSerializer.SerializeToElement(new[] { 1, 2 })).Should().Be(ConditionTruth.Undecidable);
    }

    [Fact]
    public async Task Eval_AgreesWithRouterHolds()
    {
        // J27：略過條件「Eval != Fails」與第一版 ApprovalRouter.Holds 同一組呼叫，成立、不成立、解析不了三種都要一致
        var (r, module, registry) = await Setup();
        var values = Values(r, 150000);
        var eval = ApprovalGraphMode.Eval(module, values, registry, Today, NoUsers);
        JsonElement[] whens =
        [
            Cond(r.Total, "gte", 100000), Cond(r.Total, "gte", 99999999), Cond(987654, "gte", 1),
            JsonSerializer.SerializeToElement(new { all = Array.Empty<object>() }), JsonSerializer.SerializeToElement("gte")
        ];
        foreach (var when in whens)
            (eval(when) != ConditionTruth.Fails).Should().Be(ApprovalRouter.Holds(when, module, values, registry, Today, NoUsers), when.ToString());
    }
}
