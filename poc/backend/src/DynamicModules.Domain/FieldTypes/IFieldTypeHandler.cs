using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.FieldTypes;

public interface IFieldTypeHandler
{
    FieldType Type { get; }

    /// <summary>此型別可用的篩選運算子（順序即 poc/shared/filter-operators.json 的順序）。</summary>
    IReadOnlyList<FilterOperator> Operators { get; }

    /// <summary>依欄位設定決定的運算子；預設同 Operators，公式依結果型別覆寫。</summary>
    IReadOnlyList<FilterOperator> OperatorsFor(Field field) => Operators;

    /// <summary>把已正規化的非 null 值投影成索引列；不索引的型別回空清單。</summary>
    IReadOnlyList<IndexValue> ToIndex(JsonElement value, Field field);

    /// <summary>輸入 null 或 JSON null 一律回 Ok(null)；必填檢查不在這裡。</summary>
    FieldValueResult Normalize(JsonElement? input, Field field, FieldTypeContext ctx);
}

public sealed record FieldValueResult(bool IsValid, JsonElement? Value, string? Error)
{
    public static FieldValueResult Ok(JsonElement? value) => new(true, value, null);
    public static FieldValueResult Fail(string error) => new(false, null, error);
}

public sealed record FieldTypeContext(
    IReadOnlySet<long> ActiveOptionIds,
    IReadOnlySet<long> AllOptionIds,
    IReadOnlyDictionary<long, int> OptionSequence,
    JsonElement? PreviousValue,
    IReadOnlySet<string> UserIds,
    IReadOnlySet<string>? ValidRefKeys = null,
    bool EnforceWriteLimits = true);
