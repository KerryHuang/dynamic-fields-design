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

    private void Fail(string name) => _errors.TryAdd(name, McpMessages.InvalidArgument(name));

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
