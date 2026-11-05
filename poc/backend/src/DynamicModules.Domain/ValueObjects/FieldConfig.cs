using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.ValueObjects;

/// <summary>欄位設定；依欄位型別使用其中的屬性。</summary>
public record FieldConfig
{
    public bool Required { get; init; }
    public bool ReadOnly { get; init; }
    public int? MaxLength { get; init; }
    public int? Precision { get; init; }
    public int? Scale { get; init; }
    public JsonElement? DefaultValue { get; init; }
    /// <summary>date：新增記錄沒提供值時帶 IBusinessTime.Today；不能和 DefaultValue 同時設定。</summary>
    public bool DefaultToday { get; init; }

    /// <summary>明細模組的欄位：新增列沒提供值時，帶表頭這個欄位（Field.Id）這次存檔後的值。</summary>
    public long? ParentDefaultFieldId { get; init; }

    public bool ShowInList { get; init; }
    public bool IsFilterable { get; init; }
    public bool IsSortable { get; init; }

    /// <summary>formula：公式運算式。</summary>
    public string? Formula { get; init; }

    /// <summary>formula：結果型別 number、currency、date。</summary>
    public string? ResultType { get; init; }

    /// <summary>autonumber：編號格式，如 REP-{yyyyMM}-{0000}。</summary>
    public string? Format { get; init; }

    /// <summary>reference：參照目標；建立後不能更改。</summary>
    public ReferenceTarget? RefTarget { get; init; }

    /// <summary>reference：被參照記錄刪除時的處理；null 視同 restrict。只能用在 module 目標。</summary>
    public ReferenceOnDelete? OnDelete { get; init; }

    /// <summary>reference：lookup 或 detail；null 視同 lookup。建立後不能更改。</summary>
    public ReferenceRelation? Relation { get; init; }

    /// <summary>autonumber：編號範圍；"parent" 表示在表頭內編號（明細模組用）。</summary>
    public string? Scope { get; init; }

    /// <summary>rollup：彙總設定。</summary>
    public RollupConfig? Rollup { get; init; }
}

/// <summary>彙總欄位設定：來源參照欄位、函數、被彙總欄位（count 不需要）與子記錄條件。</summary>
public record RollupConfig(long SourceFieldId, RollupFunction Function, long? AggregateFieldId, JsonElement? Condition);

/// <summary>Kind 是 module（Key 為 Module.Key）或 entity（Key 為登錄的實體鍵）。</summary>
public record ReferenceTarget(string Kind, string Key)
{
    public const string ModuleKind = "module";
    public const string EntityKind = "entity";
}
