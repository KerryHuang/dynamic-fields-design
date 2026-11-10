using System.Globalization;
using System.Text.Json;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.FieldTypes.Handlers;

namespace DynamicModules.Application.Dynamic.Display;

/// <summary>欄位值的顯示文字（歷程與匯出共用），純函式。</summary>
public static class FieldDisplay
{
    /// <summary>營運時區偏移（Asia/Taipei 無日光節約）；匯入解析日期時間也用這個。</summary>
    public static readonly TimeSpan BusinessOffset = TimeSpan.FromHours(8);

    public static string Format(
        Field field, JsonElement? value, string locale, ReferenceInfo? reference,
        IReadOnlyDictionary<string, string> userNames)
    {
        if (value is not { } v || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return "";
        return field.Type switch
        {
            FieldType.Integer => Number(v, 0),
            FieldType.Decimal or FieldType.Currency => Number(v, field.Config.Scale ?? 2),
            FieldType.Percent => Number(v, field.Config.Scale ?? 2) + "%",
            FieldType.DateTime => DateTimeText(v),
            FieldType.Boolean => Boolean(v, locale),
            FieldType.Enum => OptionLabel(field, v.GetString()!, locale),
            FieldType.MultiEnum => MultiEnum(field, v, locale),
            FieldType.User => userNames.GetValueOrDefault(v.GetString()!, v.GetString()!),
            FieldType.Reference => ReferenceText(ReferenceHandler.KeyOf(v) ?? v.GetRawText(), reference, locale),
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

    private static string DateTimeText(JsonElement v) =>
        DateTimeOffset.Parse(v.GetString()!, CultureInfo.InvariantCulture).ToOffset(BusinessOffset)
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Boolean(JsonElement v, string locale)
    {
        return ReportText.Get(locale, v.GetBoolean() ? ReportText.Yes : ReportText.No);
    }

    private static string OptionLabel(Field field, string id, string locale)
    {
        var option = field.Options.FirstOrDefault(o => $"opt_{o.Id}" == id);
        return option?.LabelI18n.Resolve(locale) ?? id;
    }

    private static string MultiEnum(Field field, JsonElement v, string locale)
    {
        var ids = v.EnumerateArray().Select(e => e.GetString()!).ToList();
        // 依選項的排序；不在選項裡的 id 排最後，保持原順序
        var ordered = ids.OrderBy(id => field.Options.FirstOrDefault(o => $"opt_{o.Id}" == id)?.Sequence ?? int.MaxValue);
        return string.Join("; ", ordered.Select(id => OptionLabel(field, id, locale)));
    }

    private static string ReferenceText(string key, ReferenceInfo? reference, string locale) => reference?.State switch
    {
        ReferenceInfo.Ok => reference.Title ?? key,
        ReferenceInfo.Restricted => ReportText.Get(locale, ReportText.NoPermission),
        ReferenceInfo.Missing => ReportText.Get(locale, ReportText.Deleted),
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
