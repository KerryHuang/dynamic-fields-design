using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Rules;

/// <summary>規則求值的結果；block 目標已展開成欄位 Id。</summary>
/// <param name="HiddenFieldIds">被 Visibility 規則隱藏的欄位。</param>
/// <param name="RequiredFieldIds">Config.Required 或 Required 規則要求必填，且沒被規則隱藏的欄位。</param>
/// <param name="ReadOnlyFieldIds">被 ReadOnly 規則（以已儲存的值求值）鎖住的欄位。</param>
/// <param name="ReadOnlyAll">整筆記錄唯讀。</param>
/// <param name="AllowedOptions">被 OptionFilter 限制的欄位 → 可選的選項 Id（多條成立取交集）；沒有成立的規則就沒有鍵。</param>
/// <param name="FailedValidations">條件不成立的 Validation 規則，依規則順序。</param>
public sealed record RuleOutcome(
    IReadOnlySet<long> HiddenFieldIds,
    IReadOnlySet<long> RequiredFieldIds,
    IReadOnlySet<long> ReadOnlyFieldIds,
    bool ReadOnlyAll,
    IReadOnlyDictionary<long, IReadOnlySet<long>> AllowedOptions,
    IReadOnlyList<(IReadOnlyList<long> TargetFieldIds, I18nText Message)> FailedValidations);
