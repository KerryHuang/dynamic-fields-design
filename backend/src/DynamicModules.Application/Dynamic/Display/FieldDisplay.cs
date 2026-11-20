using System.Globalization;
using System.Text.Json;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.FieldTypes.Handlers;

namespace DynamicModules.Application.Dynamic.Display;

/// <summary>
/// 顯示用的環境：輸出語系、營運時區偏移與語系退回鏈（同一次輸出用同一份）。由 <c>CachedSystemPolicy.Display</c> 建立；
/// LocaleFallbacks 為 null 時退回繁中（同 I18nText.Resolve）。系統字（是／否、已刪除、無權限）經 Messages 沿同一條鏈取；
/// null 用只看內建層的 <see cref="BuiltinMessageCatalog"/>（測試直接建的情況，語系包 i4c 計畫判斷 4、7）。
/// </summary>
public sealed record FieldDisplayContext(
    string Locale, TimeSpan BusinessOffset, IReadOnlyList<string>? LocaleFallbacks = null, IMessageCatalog? Messages = null)
{
    internal string Text(string key) =>
        (Messages ?? BuiltinMessageCatalog.Instance).Get(new Domain.ValueObjects.LocaleContext(Locale, LocaleFallbacks ?? []), key);
}

/// <summary>欄位值的顯示文字（歷程與匯出共用），純函式。</summary>
public static class FieldDisplay
{
    public static string Format(
        Field field, JsonElement? value, FieldDisplayContext display, ReferenceInfo? reference,
        IReadOnlyDictionary<string, string> userNames)
    {
        if (value is not { } v || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return "";
        return field.Type switch
        {
            FieldType.Integer => Number(v, 0),
            FieldType.Decimal or FieldType.Currency => Number(v, field.Config.Scale ?? 2),
            FieldType.Percent => Number(v, field.Config.Scale ?? 2) + "%",
            FieldType.DateTime => DateTimeText(v, display.BusinessOffset),
            FieldType.Boolean => Boolean(v, display),
            FieldType.Enum => OptionLabel(field, v.GetString()!, display),
            FieldType.MultiEnum => MultiEnum(field, v, display),
            FieldType.User => userNames.GetValueOrDefault(v.GetString()!, v.GetString()!),
            FieldType.Reference => ReferenceText(ReferenceHandler.KeyOf(v) ?? v.GetRawText(), reference, display),
            FieldType.Formula or FieldType.Rollup => Computed(field, v),
            FieldType.Attachment => string.Join("; ", AttachmentValue.Parse(v).Select(f => f.Name)),
            _ => v.GetString() ?? v.GetRawText(),
        };
    }

    /// <summary>xlsx 的數字儲存格：integer、decimal、currency，以及結果型別為 number／currency 的 formula、rollup。</summary>
    public static bool IsNumericCell(Field field) => field.Type switch
    {
        FieldType.Integer or FieldType.Decimal or FieldType.Currency => true,
        FieldType.Formula or FieldType.Rollup => field.Config.ResultType is "number" or "currency",
        _ => false,
    };

    private static string Number(JsonElement v, int scale) =>
        v.GetDecimal().ToString("F" + scale, CultureInfo.InvariantCulture);

    /// <summary>
    /// 換成營運時區的牆上時間。顯示用的解析不拋例外：換算後超出 DateTime 範圍（例如只留在歷程裡的 9999 年底舊值，改成更東的時區之後）
    /// 就照原本的偏移顯示並標出偏移；解析不了照原文。
    /// </summary>
    private static string DateTimeText(JsonElement v, TimeSpan offset)
    {
        var s = v.GetString()!;
        if (BusinessClock.TryParseWallClock(s, offset, out var at)) return at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)
            ? dto.DateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " " + Domain.Policies.PolicyCatalog.FormatUtcOffset(dto.Offset)
            : s;
    }

    private static string Boolean(JsonElement v, FieldDisplayContext display) => display.Text(v.GetBoolean() ? ReportText.Yes : ReportText.No);

    private static string OptionLabel(Field field, string id, FieldDisplayContext display)
    {
        var option = field.Options.FirstOrDefault(o => $"opt_{o.Id}" == id);
        return option?.LabelI18n.Resolve(display.Locale, display.LocaleFallbacks) ?? id;
    }

    private static string MultiEnum(Field field, JsonElement v, FieldDisplayContext display)
    {
        var ids = v.EnumerateArray().Select(e => e.GetString()!).ToList();
        // 依選項的排序；不在選項裡的 id 排最後，保持原順序
        var ordered = ids.OrderBy(id => field.Options.FirstOrDefault(o => $"opt_{o.Id}" == id)?.Sequence ?? int.MaxValue);
        return string.Join("; ", ordered.Select(id => OptionLabel(field, id, display)));
    }

    private static string ReferenceText(string key, ReferenceInfo? reference, FieldDisplayContext display) => reference?.State switch
    {
        ReferenceInfo.Ok => reference.Title ?? key,
        ReferenceInfo.Restricted => display.Text(ReportText.NoPermission),
        ReferenceInfo.Missing => display.Text(ReportText.Deleted),
        _ => key,
    };

    /// <summary>formula、rollup 依結果型別（number／currency／date）格式化。</summary>
    private static string Computed(Field field, JsonElement v)
    {
        var scale = field.Config.Scale ?? 2;
        return field.Config.ResultType switch
        {
            "date" => v.GetString() ?? v.GetRawText(),
            _ => Number(v, scale),
        };
    }
}
