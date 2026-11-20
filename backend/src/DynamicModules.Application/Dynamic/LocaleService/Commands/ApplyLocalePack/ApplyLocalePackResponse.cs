using System.Text.Json.Serialization;

namespace DynamicModules.Application.Dynamic.LocaleService.Commands.ApplyLocalePack;

/// <summary>
/// 套用結果（i4d1 計畫判斷 12）：<see cref="PackId"/>＝套用後的目前版 Id（<see cref="NoChanges"/> 或只改語系欄位時是原本的目前版，沒有任何版本時 null，明示輸出）；
/// <see cref="Enabled"/>＝這個語系是否在 <c>locale.enabled</c> 的生效值裡（前端用它決定顯示「現在啟用」）。
/// </summary>
public sealed record ApplyLocalePackResponse(string Code, bool IsNew, bool NoChanges,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? PackId, bool Enabled);
