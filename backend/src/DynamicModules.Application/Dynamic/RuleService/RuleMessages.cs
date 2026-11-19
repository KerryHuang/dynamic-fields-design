namespace DynamicModules.Application.Dynamic.RuleService;

/// <summary>欄位規則設計存檔的訊息；上限是政策 A16（design.maxRulesPerModule）的生效值。</summary>
public static class RuleMessages
{
    public static string TooManyRules(int max) => $"規則最多 {max} 條";
}
