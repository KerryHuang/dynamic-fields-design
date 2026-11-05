namespace DynamicModules.Domain.FieldTypes;

/// <summary>篩選運算子；JSON 名稱是 camelCase（例如 notIn、withinNextDays）。</summary>
public enum FilterOperator
{
    Eq, Ne, In, NotIn, Gt, Gte, Lt, Lte, Between, Contains, StartsWith,
    WithinNextDays, WithinLastDays, IsEmpty, IsNotEmpty
}
