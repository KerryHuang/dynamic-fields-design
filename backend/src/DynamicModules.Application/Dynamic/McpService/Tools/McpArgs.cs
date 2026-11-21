using System.Text.Json;
using DynamicModules.Application.Common;

namespace DynamicModules.Application.Dynamic.McpService.Tools;

/// <summary>
/// 工具參數的型別、必填、範圍檢查（mcp1a 計畫判斷 19 第 4 步），不另引 JSON Schema 套件。錯誤收進 <see cref="Errors"/>，
/// 鍵是參數名、訊息是「參數 X 不正確」；工具有錯就回 <see cref="McpToolOutcome.InvalidArguments"/>。
/// </summary>
public sealed class McpArgs
{
    /// <summary>參數本身不是物件時的錯誤鍵。</summary>
    public const string ArgumentsKey = "arguments";

    /// <summary>寫入工具的冪等鍵參數（mcp2a 判斷 5）。</summary>
    public const string IdempotencyKey = "idempotencyKey";

    /// <summary>冪等鍵格式，沿用 <see cref="OperationContext.KeyPattern"/>（與 REST 的 Idempotency-Key 標頭同一條）。</summary>
    public static bool IsIdempotencyKey(string? key) =>
        key is not null && System.Text.RegularExpressions.Regex.IsMatch(key, OperationContext.KeyPattern);

    private readonly JsonElement _args;
    private readonly Dictionary<string, MessageRef> _errors = new(StringComparer.Ordinal);

    private McpArgs(JsonElement args)
    {
        _args = args;
        if (args.ValueKind != JsonValueKind.Object) Fail(ArgumentsKey);
    }

    public static McpArgs Of(JsonElement args) => new(args);

    public bool HasErrors => _errors.Count > 0;

    public IReadOnlyDictionary<string, MessageRef> Errors => _errors;

    public McpToolOutcome Invalid() => McpToolOutcome.InvalidArguments(_errors);

    private bool TryGet(string name, out JsonElement value)
    {
        value = default;
        return _args.ValueKind == JsonValueKind.Object && _args.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;
    }

    /// <summary>參數有沒有帶（含明確的 JSON null）：「帶 null 代表清除」的參數用它分辨沒帶與清除（mcp2a 判斷 2）。</summary>
    public bool Has(string name) => _args.ValueKind == JsonValueKind.Object && _args.TryGetProperty(name, out _);

    public void Fail(string name) => _errors.TryAdd(name, McpMessages.InvalidArgument(name));

    public string? String(string name, bool required = false, int maxLength = 200)
    {
        if (!TryGet(name, out var v)) { if (required) Fail(name); return null; }
        if (v.ValueKind != JsonValueKind.String || v.GetString() is not { } s || (required && s.Length == 0) || s.Length > maxLength)
        {
            Fail(name);
            return null;
        }
        return s;
    }

    public long? Long(string name, bool required = false, long min = 1, long max = long.MaxValue)
    {
        if (!TryGet(name, out var v)) { if (required) Fail(name); return null; }
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var n) || n < min || n > max)
        {
            Fail(name);
            return null;
        }
        return n;
    }

    public int? Int(string name, int min, int max, bool required = false) => (int?)Long(name, required, min, max);

    public bool? Bool(string name)
    {
        if (!TryGet(name, out var v)) return null;
        if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.GetBoolean();
        Fail(name);
        return null;
    }

    /// <summary>物件型參數（例如篩選條件樹）；不是物件記錯誤。</summary>
    public JsonElement? Object(string name)
    {
        if (!TryGet(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Object) return v.Clone();
        Fail(name);
        return null;
    }

    /// <summary>列舉型參數：沒帶回 <paramref name="fallback"/>；不在 <paramref name="values"/> 裡記錯誤。</summary>
    public string Choice(string name, IReadOnlyCollection<string> values, string fallback)
    {
        var v = String(name);
        if (v is null) return fallback;
        if (values.Contains(v)) return v;
        Fail(name);
        return fallback;
    }

    /// <summary>日期參數（yyyy-MM-dd）。</summary>
    public DateOnly? Date(string name)
    {
        var v = String(name, maxLength: 10);
        if (v is null) return null;
        if (DateOnly.TryParseExact(v, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d)) return d;
        Fail(name);
        return null;
    }

    /// <summary>陣列型參數；不是陣列記錯誤。</summary>
    public IReadOnlyList<JsonElement>? Array(string name)
    {
        if (!TryGet(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Array) return v.EnumerateArray().Select(e => e.Clone()).ToList();
        Fail(name);
        return null;
    }

    /// <summary>
    /// 鍵 → 字串陣列的物件（送簽的 <c>picks</c>：關卡代號 → userId，mcp2a 判斷 2）。沒帶回 null；不是物件、值不是字串陣列記錯誤，
    /// 錯誤鍵是 <c>&lt;參數&gt;.&lt;鍵&gt;</c>。
    /// </summary>
    public Dictionary<string, List<string>>? StringLists(string name)
    {
        if (Object(name) is not { } o) return null;
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var p in o.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Array || p.Value.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
            {
                Fail($"{name}.{p.Name}");
                continue;
            }
            result[p.Name] = p.Value.EnumerateArray().Select(e => e.GetString()!).ToList();
        }
        return result;
    }

    /// <summary>
    /// 欄位值物件（mcp2a 判斷 16）：鍵是欄位 key、值原樣（JSON null 是清除），交給既有寫入請求解析。沒帶回空物件；不是物件記錯誤。
    /// </summary>
    public Dictionary<string, JsonElement?> Values(string name) =>
        Object(name) is { } o ? ToValues(o) : new Dictionary<string, JsonElement?>(StringComparer.Ordinal);

    private static Dictionary<string, JsonElement?> ToValues(JsonElement o) =>
        o.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.Null ? (JsonElement?)null : p.Value.Clone(),
            StringComparer.Ordinal);

    /// <summary>明細列參數的一列（mcp2a 判斷 2、16）：沒有 <see cref="Id"/> 是新增。</summary>
    public sealed record DetailRow(long? Id, string? RowVersion, Dictionary<string, JsonElement?> Values);

    /// <summary>明細列的刪除旗標（REST 的 <c>_delete</c>）：MCP 不收（mcp2a D3），帶了就是參數錯誤。</summary>
    public const string DetailDeleteKey = "_delete";

    /// <summary>
    /// 明細參數 <c>{ 明細模組 key: [ { id?, rowVersion?, values } ] }</c>（mcp2a 判斷 2、16）：每列只讀 <c>id</c>、<c>rowVersion</c>、<c>values</c>，
    /// 其他鍵不採用；帶 <see cref="DetailDeleteKey"/> 記錯誤（D3）。<paramref name="allowId"/> 是 false（新增記錄）時帶 <c>id</c> 也記錯誤。
    /// 沒帶回 null。錯誤鍵是 <c>details.&lt;明細&gt;[i].&lt;參數&gt;</c>。
    /// </summary>
    public Dictionary<string, List<DetailRow>>? Details(string name, bool allowId)
    {
        if (Object(name) is not { } o) return null;
        var result = new Dictionary<string, List<DetailRow>>(StringComparer.Ordinal);
        foreach (var module in o.EnumerateObject())
        {
            var at = $"{name}.{module.Name}";
            if (module.Value.ValueKind != JsonValueKind.Array) { Fail(at); continue; }
            var rows = new List<DetailRow>();
            var i = 0;
            foreach (var row in module.Value.EnumerateArray())
            {
                var rowAt = $"{at}[{i++}]";
                if (row.ValueKind != JsonValueKind.Object) { Fail(rowAt); continue; }
                if (row.TryGetProperty(DetailDeleteKey, out _)) { Fail($"{rowAt}.{DetailDeleteKey}"); continue; }
                long? id = null;
                if (row.TryGetProperty("id", out var idEl) && idEl.ValueKind != JsonValueKind.Null)
                {
                    if (!allowId || idEl.ValueKind != JsonValueKind.Number || !idEl.TryGetInt64(out var n) || n < 1) { Fail($"{rowAt}.id"); continue; }
                    id = n;
                }
                string? rowVersion = null;
                if (row.TryGetProperty("rowVersion", out var rv) && rv.ValueKind != JsonValueKind.Null)
                {
                    if (rv.ValueKind != JsonValueKind.String || rv.GetString() is not { Length: > 0 and <= 200 } s) { Fail($"{rowAt}.rowVersion"); continue; }
                    rowVersion = s;
                }
                if (!row.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object) { Fail($"{rowAt}.values"); continue; }
                rows.Add(new DetailRow(id, rowVersion, ToValues(values)));
            }
            result[module.Name] = rows;
        }
        return result;
    }

    /// <summary>
    /// 分頁大小（判斷 14、20）：明確傳入時要在 1–<paramref name="toolMax"/> 之間，否則記錯誤；沒帶就是 <paramref name="maxRows"/>。
    /// 送出值一律 <c>min(值, maxRows)</c>（政策 <c>mcp.maxRows</c>）。
    /// </summary>
    public int PageSize(int toolMax, int maxRows, string name = "pageSize")
    {
        var requested = Int(name, 1, toolMax);
        return Math.Min(requested ?? maxRows, maxRows);
    }
}
