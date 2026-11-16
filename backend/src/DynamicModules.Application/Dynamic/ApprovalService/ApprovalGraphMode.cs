using System.Text.Json;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Rules;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>graph 執行期的共用輔助（計畫 fg2 J2、J27）：推進、重新比對、預覽只引用這裡，不各寫一份。</summary>
public static class ApprovalGraphMode
{
    /// <summary>
    /// 這個實例走圖引擎嗎：綁定的定義是 graph，而且實例走流程（不是備援）。備援實例（§5.5 第 5 點）沿用舊的綁定版本，
    /// 只有一關或簽，照第一版；只看 Shape 會把單一關的備援誤派到圖引擎。
    /// </summary>
    public static bool Applies(ApprovalInstance inst, ApprovalDefinition def) =>
        def.Shape == ApprovalShapes.Graph && inst.RouteKind == ApprovalRouteKind.Flow;

    /// <summary>能力守門（U2）：引擎支援的節點型別，傳給 <see cref="ApprovalGraphRuntime.Build"/>；fg5 加 notify。</summary>
    public static readonly IReadOnlySet<string> SupportedKinds = new HashSet<string>
    {
        ApprovalNodeKinds.Start, ApprovalNodeKinds.End, ApprovalNodeKinds.Approval,
        ApprovalNodeKinds.Condition, ApprovalNodeKinds.Fork, ApprovalNodeKinds.Join
    };

    /// <summary>
    /// 條件求值轉接（J27），與 <see cref="ApprovalRouter.Match"/>、<see cref="ApprovalRouter.Holds"/> 同一組呼叫：
    /// 不是物件、或解析有錯 → Undecidable；空群組（Node 為 null）→ Holds；其餘依 <see cref="ConditionEvaluator"/> 回 Holds 或 Fails。
    /// 「無法判斷」的意思依呼叫端而定：條件節點的出線交給 Choose（Undecidable 就是無法判斷，409）；
    /// 簽核節點的略過條件 <c>Eval != Fails</c> 就算成立（解析不了視為成立，寧可多簽一關）。
    /// </summary>
    public static Func<JsonElement, ConditionTruth> Eval(Module module, IReadOnlyDictionary<string, JsonElement?> values,
        FieldTypeRegistry registry, DateOnly today, IReadOnlySet<string> userIds) => when =>
    {
        if (when.ValueKind != JsonValueKind.Object) return ConditionTruth.Undecidable;
        var parsed = ConditionParser.Parse(when, module, registry, ConditionUsage.Rule, userIds);
        if (parsed.Error is not null) return ConditionTruth.Undecidable;
        return parsed.Node is null || ConditionEvaluator.Evaluate(parsed.Node, values, registry, today) ? ConditionTruth.Holds : ConditionTruth.Fails;
    };
}
