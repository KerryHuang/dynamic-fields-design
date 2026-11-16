using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Conditions;

public static class ConditionFields
{
    /// <summary>條件樹引用到的所有欄位（含巢狀群組）。</summary>
    public static IEnumerable<Field> Collect(ConditionNode node) => node switch
    {
        FieldCondition c => [c.Field],
        ConditionGroup g => g.Children.SelectMany(Collect),
        _ => []
    };
}
