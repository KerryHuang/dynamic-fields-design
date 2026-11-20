using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Localization;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ImportService;

/// <summary>
/// 匯入時預先查好的對照表（由呼叫端建立，parser 保持純函式）。
/// <para><see cref="UserIdByName"/>：鍵是 DisplayName、值是 UserId；文字等於任一個都接受。</para>
/// <para><see cref="RefKeysByTitle"/>：[欄位 Id][標題] → 同名的參照鍵（字串）；標題完全相等才算。</para>
/// <para><see cref="BusinessOffset"/>：營運時區偏移（政策 locale.timeZone），不帶偏移的日期時間文字視為這個時區。</para>
/// <para><see cref="Words"/>：是否欄位接受的文字（固定別名＋各已安裝語系的是／否）。</para>
/// </summary>
public sealed record ImportLookups(
    IReadOnlyDictionary<string, string> UserIdByName,
    IReadOnlyDictionary<long, IReadOnlyDictionary<string, IReadOnlyList<string>>> RefKeysByTitle,
    TimeSpan BusinessOffset,
    BooleanWords Words);

/// <summary>
/// 是否欄位接受的文字（語系包 i4c Q2）：固定別名「是、true、yes、1」／「否、false、no、0」加上每個已安裝語系（含已停用）匯出的
/// <see cref="ReportText.Yes"/>／<see cref="ReportText.No"/>，不分大小寫。兩邊都有的字（含撞到固定別名）一律無法判斷，
/// 不讓語系包的錯譯悄悄把「否」判成「是」（資料完整性守衛）。
/// </summary>
public sealed class BooleanWords
{
    private static readonly string[] FixedYes = ["是", "true", "yes", "1"], FixedNo = ["否", "false", "no", "0"];

    private readonly HashSet<string> _yes, _no, _ambiguous;

    private BooleanWords(IEnumerable<(string Yes, string No)> perLocale)
    {
        var words = perLocale.ToList();
        _yes = new HashSet<string>(FixedYes.Concat(words.Select(w => w.Yes)), StringComparer.OrdinalIgnoreCase);
        _no = new HashSet<string>(FixedNo.Concat(words.Select(w => w.No)), StringComparer.OrdinalIgnoreCase);
        // 固定別名併進兩邊之後才算撞字
        _ambiguous = new HashSet<string>(_yes.Where(_no.Contains), StringComparer.OrdinalIgnoreCase);
    }

    public static BooleanWords From(IEnumerable<(string Yes, string No)> perLocale) => new(perLocale);

    /// <summary>同時出現在「是」與「否」兩邊的字（含固定別名，不分大小寫）；語系包分析用它判斷撞字（票 #68，i4d1 計畫判斷 6）。</summary>
    public IReadOnlySet<string> Ambiguous => _ambiguous;

    /// <summary>內建四語（經 <see cref="BuiltinMessageCatalog"/>）；沒有 DI 的呼叫端與測試用。</summary>
    public static BooleanWords Builtin { get; } = From(BuiltinLocales.Codes.Select(l => new LocaleContext(l, []))
        .Select(lc => (BuiltinMessageCatalog.Instance.Get(lc, ReportText.Yes), BuiltinMessageCatalog.Instance.Get(lc, ReportText.No))));

    /// <summary>true／false；撞字或不認得回 null。</summary>
    public bool? Parse(string text) =>
        _ambiguous.Contains(text) ? null : _yes.Contains(text) ? true : _no.Contains(text) ? false : null;
}

/// <summary>匯入值解析（FieldDisplay.Format 的反向）：只把文字轉成儲存的 JSON 形狀，驗證交給型別處理器。</summary>
public static class FieldImportParser
{
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "yyyy/M/d"];
    private static readonly Regex HasOffset = new(@"T.*(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant);
    // 純數字，或每三位一個逗號的千分位；"1,5"、"1,23" 這類分組錯誤一律不收
    private static readonly Regex NumberText = new(@"^-?(\d+|\d{1,3}(,\d{3})+)(\.\d+)?$", RegexOptions.CultureInvariant);

    public static (JsonElement? Value, string? Error) Parse(Field field, string text, ImportLookups lookups)
    {
        text = text.Trim();
        if (text.Length == 0) return (null, null);
        return field.Type switch
        {
            FieldType.Text or FieldType.Textarea or FieldType.Email or FieldType.Phone or FieldType.Url =>
                Ok(StripInjectionApostrophe(text)),
            FieldType.Integer or FieldType.Decimal or FieldType.Currency => Number(text, percent: false),
            FieldType.Percent => Number(text, percent: true),
            FieldType.Boolean => Boolean(text, lookups.Words),
            FieldType.Date => Date(text),
            FieldType.DateTime => DateTime(text, lookups.BusinessOffset),
            FieldType.Time => Time(text),
            FieldType.Enum => Enum(field, StripInjectionApostrophe(text)),
            FieldType.MultiEnum => MultiEnum(field, StripInjectionApostrophe(text)),
            FieldType.User => User(StripInjectionApostrophe(text), lookups),
            FieldType.Reference => Reference(field, StripInjectionApostrophe(text), lookups),
            FieldType.Attachment => (null, "附件欄位不支援匯入"),
            _ => (null, "系統計算的欄位"),
        };
    }

    private static (JsonElement?, string?) Ok<T>(T value) => (JsonSerializer.SerializeToElement(value), null);

    private static (JsonElement?, string?) Fail(string error) => (null, error);

    /// <summary>匯出時為擋公式注入加的 '：只在 ' 後面接 = + - @ 時還原。ImportPlanner 預查參照標題時也用這個。</summary>
    public static string StripInjectionApostrophe(string text) =>
        text.Length >= 2 && text[0] == '\'' && text[1] is '=' or '+' or '-' or '@' ? text[1..] : text;

    private static (JsonElement?, string?) Number(string text, bool percent)
    {
        if (percent && text.EndsWith('%')) text = text[..^1].TrimEnd();
        return NumberText.IsMatch(text) && decimal.TryParse(text.Replace(",", ""),
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d)
            ? Ok(d)
            : Fail("請輸入數字");
    }

    // 各已安裝語系匯出的是／否文字（FieldDisplay 經 catalog）都要能原樣匯回
    private static (JsonElement?, string?) Boolean(string text, BooleanWords words) =>
        words.Parse(text) is { } b ? Ok(b) : Fail("請輸入「是」或「否」");

    private static (JsonElement?, string?) Date(string text) =>
        System.DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? Ok(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            : Fail("日期格式必須是 yyyy-MM-dd");

    private static (JsonElement?, string?) Time(string text) =>
        System.DateTime.TryParseExact(text, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? Ok(t.ToString("HH:mm", CultureInfo.InvariantCulture))
            : Fail("時間格式必須是 H:mm");

    /// <summary>`yyyy-MM-dd HH:mm` 視為營運時區；ISO 8601 必須帶 Z 或偏移，轉成營運偏移的儲存形狀。</summary>
    private static (JsonElement?, string?) DateTime(string text, TimeSpan offset)
    {
        DateTimeOffset dto;
        if (System.DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            // 營運時區的牆上時間換成 UTC 後超出 DateTime 範圍時，new DateTimeOffset 會丟例外：先檢查，回逐列的欄位錯誤
            if (offset < TimeSpan.Zero ? local > System.DateTime.MaxValue + offset : local < System.DateTime.MinValue + offset)
                return Fail("日期時間格式錯誤");
            dto = new DateTimeOffset(local, offset);
        }
        else if (!HasOffset.IsMatch(text) || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out dto))
            return Fail("日期時間格式錯誤");
        if (BusinessClock.Overflows(dto, offset)) return Fail("日期時間格式錯誤");
        return Ok(dto.ToOffset(offset).ToString("yyyy-MM-dd\\THH:mm:ss", CultureInfo.InvariantCulture) + PolicyCatalog.FormatUtcOffset(offset));
    }

    private static (JsonElement?, string?) Enum(Field field, string text)
    {
        var (id, error) = FindOption(field, text);
        return error is null ? Ok(id) : Fail(error);
    }

    private static (JsonElement?, string?) MultiEnum(Field field, string text)
    {
        var ids = new List<string>();
        foreach (var part in text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var (id, error) = FindOption(field, part);
            if (error is not null) return Fail(error);
            ids.Add(id!);
        }
        return ids.Count == 0 ? (null, null) : Ok(ids);
    }

    /// <summary>任一語系的標籤、不分大小寫；含停用的選項（停用與否交給型別處理器）。</summary>
    private static (string? Id, string? Error) FindOption(Field field, string text)
    {
        var hits = field.Options
            .Where(o => o.LabelI18n.Values.Values.Any(l => l.Trim().Equals(text, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return hits.Count switch
        {
            0 => (null, $"找不到選項「{text}」"),
            1 => ($"opt_{hits[0].Id}", null),
            _ => (null, $"選項「{text}」不只一個"),
        };
    }

    private static (JsonElement?, string?) User(string text, ImportLookups lookups)
    {
        if (lookups.UserIdByName.TryGetValue(text, out var id)) return Ok(id);
        return lookups.UserIdByName.Values.Contains(text) ? Ok(text) : Fail($"找不到使用者「{text}」");
    }

    private static (JsonElement?, string?) Reference(Field field, string text, ImportLookups lookups)
    {
        if (!lookups.RefKeysByTitle.TryGetValue(field.Id, out var titles)
            || !titles.TryGetValue(text, out var keys) || keys.Count == 0)
            return Fail($"找不到「{text}」");
        if (keys.Count > 1) return Fail($"「{text}」有多筆同名資料");
        // 模組參照存記錄 Id（number），實體參照存主鍵（string）
        return field.Config.RefTarget?.Kind == ReferenceTarget.ModuleKind && long.TryParse(keys[0], out var n)
            ? Ok(n)
            : Ok(keys[0]);
    }
}
