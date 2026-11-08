namespace DynamicModules.Domain.FieldTypes;

/// <summary>篩選運算子；JSON 名稱是 camelCase（例如 notIn、withinNextDays）。BeforeToday 是嚴格早於業務日，不帶值。</summary>
public enum FilterOperator
{
    Eq, Ne, In, NotIn, Gt, Gte, Lt, Lte, Between, Contains, StartsWith,
    WithinNextDays, WithinLastDays, IsEmpty, IsNotEmpty, BeforeToday
}
