using System.Text.Json.Serialization;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.LocaleService.Packs;
using MediatR;

namespace DynamicModules.Application.Dynamic.LocaleService.Queries.GetLocales;

/// <summary>
/// 語系清單（<c>GET i18n/locales</c>；語系包設計 §6.5、i4d1 計畫 Task 8）：讀資料庫（<c>LocaleWorldReader</c>＋目前版與上一版的中繼欄位），
/// 不讀快照，寫入後馬上重取就是新狀態。順序同快照：內建在前、匯入依安裝時間。
/// </summary>
public sealed record GetLocalesRequest : IRequest<ServiceResult>, ISystemAdminRequest;

public sealed record LocaleAdminList(bool ImportEnabled, int PackMaxBytes, int MaxInstalled, IReadOnlyList<LocaleAdminItem> Locales);

/// <summary>
/// <see cref="Origin"/> 是 <c>builtin</c> 或 <c>installed</c>；<see cref="Overrides"/> 是目前版的鍵數；沒有任何版本時 <see cref="Current"/> 是 null（明示輸出）。
/// i4d2（計畫判斷 6）：<see cref="Completeness"/> 用濾過基準鍵的覆寫層（含缺最多的分類）；<see cref="Review"/> 從目前版原始列算（沒有版本時全 0）；
/// <see cref="BuiltFor"/> 是目前版的 <c>pack.builtFor</c>（沒有時明示 null）。
/// </summary>
public sealed record LocaleAdminItem(
    string Code, string NativeName, string Origin,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FallbackCode,
    bool Enabled, bool IsDefault, bool Required, LocaleOverrideCounts Overrides,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] LocalePackSummary? Current,
    bool HasPrevious, CompletenessReport Completeness, OverlayReview Review,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? BuiltFor);

public sealed record LocaleOverrideCounts(int Ui, int Server);

/// <summary>目前版的中繼欄位；移除覆寫產生的空版本沒有檔案資訊（<see cref="PackVersion"/>、<see cref="FileName"/> 是 null，明示輸出）。</summary>
public sealed record LocalePackSummary(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? PackVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FileName,
    DateTime ImportedUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ImportedBy);
