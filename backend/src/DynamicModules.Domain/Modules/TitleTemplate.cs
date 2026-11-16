using System.Globalization;
using System.Text;
using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Modules;

/// <summary>
/// 模組的標題樣板，例如 <c>{no} {name}</c>：{} 內是欄位，其餘文字照原樣。資料庫（Module.TitleTemplate）存欄位 Id（<c>{12} {13}</c>，
/// 同公式），套件存欄位代號。記錄標題照樣板組成後存進 Record.Title，所以列表、參照顯示、挑選器搜尋、匯入比對、報表標籤都直接用它。
/// </summary>
public static class TitleTemplate
{
    /// <summary>
    /// 樣板本身的上限：大括號外的文字加上每個欄位算 1 字。不量欄位寫法的長度，因為同一份樣板在資料庫是 {Id}、在套件是 {代號}，
    /// 長度不同；量字元數會讓一邊合法的樣板換成另一邊就超過（開啟或再次匯入時被擋）。
    /// </summary>
    public const int MaxLength = 200;

    /// <summary>大括號內最多幾個字：欄位代號最多 40 字，欄位 Id 最多 19 位數。</summary>
    public const int MaxTokenLength = 40;

    /// <summary>
    /// 內部表示的上限：每個欄位連大括號最多 <see cref="MaxTokenLength"/> + 2 字，所以任何通過 <see cref="MaxLength"/> 的樣板
    /// 不論寫成 Id 還是代號（含大括號內的空白）都放得進資料庫欄位。
    /// </summary>
    public const int MaxStoredLength = MaxLength * (MaxTokenLength + 2);

    public const string TooLongMessage = "標題樣板不能超過 200 字（每個欄位算 1 字）";

    /// <summary>可以放進樣板的型別：值本身就是一段文字（下拉取選項名稱）。</summary>
    public static readonly IReadOnlySet<FieldType> AllowedTypes = new HashSet<FieldType>
    {
        FieldType.Text, FieldType.Email, FieldType.Phone, FieldType.Url, FieldType.AutoNumber, FieldType.Date, FieldType.Enum
    };

    /// <summary>錯誤訊息用的說明，跟 <see cref="AllowedTypes"/> 一致。</summary>
    public const string AllowedTypesText = "只能用文字、電子郵件、電話、網址、自動編號、日期或單選下拉欄位";

    /// <summary>Text 或 Token 二擇一。</summary>
    public sealed record Part(string? Text, string? Token);

    /// <summary>拆成文字與欄位；Error 不含位置前綴。</summary>
    public static (IReadOnlyList<Part> Parts, string? Error) Parse(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return ([], "標題樣板是空的");
        if (template.Length > MaxStoredLength) return ([], TooLongMessage);   // 先擋明顯過長的輸入，不必逐字解析
        var parts = new List<Part>();
        var text = new StringBuilder();
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '}') return ([], "標題樣板的大括號沒有成對");
            if (c != '{') { text.Append(c); continue; }
            var end = template.IndexOfAny(['{', '}'], i + 1);
            if (end < 0 || template[end] == '{') return ([], "標題樣板的大括號沒有成對");
            if (end - i - 1 > MaxTokenLength) return ([], $"標題樣板的 {{}} 內最多 {MaxTokenLength} 字");   // 連空白一起量，存進資料庫的是原字串
            var token = template[(i + 1)..end].Trim();
            if (token.Length == 0) return ([], "標題樣板有空的 {}");
            if (text.Length > 0) { parts.Add(new Part(text.ToString(), null)); text.Clear(); }
            parts.Add(new Part(null, token));
            i = end;
        }
        if (text.Length > 0) parts.Add(new Part(text.ToString(), null));
        if (parts.Sum(p => p.Text?.Length ?? 1) > MaxLength) return ([], TooLongMessage);
        return parts.Any(p => p.Token is not null) ? (parts, null) : ([], "標題樣板至少要引用一個欄位");
    }

    /// <summary>樣板引用的欄位（依出現順序）；樣板不合法時是空的。</summary>
    public static IReadOnlyList<string> Tokens(string? template) =>
        Parse(template).Parts.Where(p => p.Token is not null).Select(p => p.Token!).ToList();

    /// <summary>資料庫的樣板引用的欄位 Id；不是數字的忽略。</summary>
    public static IReadOnlyList<long> FieldIds(string? template) =>
        Tokens(template).Select(t => long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : (long?)null)
            .Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();

    /// <summary>把每個欄位換掉（代號 ↔ Id）；有一個換不到就回 null。</summary>
    public static string? Map(string template, Func<string, string?> map)
    {
        var (parts, error) = Parse(template);
        if (error is not null) return null;
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            if (p.Text is not null) { sb.Append(p.Text); continue; }
            if (map(p.Token!) is not { } mapped) return null;
            sb.Append('{').Append(mapped).Append('}');
        }
        return sb.ToString();
    }

    /// <summary>
    /// 依 module.TitleTemplate 組出標題：文字類取值、日期取存的 yyyy-MM-dd、下拉取選項的繁中名稱；沒值或找不到的欄位是空字串，
    /// 結果去掉頭尾空白（長度由呼叫端截斷）。
    /// </summary>
    public static string Render(Module module, IReadOnlyDictionary<string, JsonElement?> values)
    {
        var fields = module.Blocks.SelectMany(b => b.Fields).ToDictionary(f => f.Id.ToString(CultureInfo.InvariantCulture));
        var sb = new StringBuilder();
        foreach (var p in Parse(module.TitleTemplate).Parts)
        {
            if (p.Text is not null) { sb.Append(p.Text); continue; }
            if (fields.TryGetValue(p.Token!, out var field)) sb.Append(ValueText(field, values));
        }
        return sb.ToString().Trim();
    }

    private static string ValueText(Field field, IReadOnlyDictionary<string, JsonElement?> values)
    {
        if (!values.TryGetValue(field.Key, out var v) || v is not { ValueKind: JsonValueKind.String } s) return "";
        var text = s.GetString()!;
        if (field.Type != FieldType.Enum) return text;
        return text.StartsWith("opt_", StringComparison.Ordinal)
            && long.TryParse(text.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && field.Options.FirstOrDefault(o => o.Id == id) is { } option
                ? option.LabelI18n.Resolve(I18nText.FallbackLocale)
                : "";
    }
}
