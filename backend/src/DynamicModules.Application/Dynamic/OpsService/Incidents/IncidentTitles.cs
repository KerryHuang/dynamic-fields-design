using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Application.Dynamic.OpsService.Health;

namespace DynamicModules.Application.Dynamic.OpsService.Incidents;

/// <summary>
/// 事件標題的唯一組字處（維運設計 §17.2.2、O2-T22）：各來源只接受白名單參數，值只能是代碼、數字或路由樣板（<c>{id}</c> 形式），
/// 不接受任意字串。畫面與鈴鐺依語系組字；這裡另組一份繁中文字，只給站內通知的 <c>TitleSnapshot</c> 當退路。
/// </summary>
public static partial class IncidentTitles
{
    public const string ServerError = "ops.title.serverError", ClientError = "ops.title.clientError",
        AdminChanged = "ops.title.adminChanged", HealthPrefix = "ops.title.health.";

    /// <summary>JavaScript 內建錯誤名稱；其他一律換成 <c>Error</c>（§17.2.2 標題表）。</summary>
    private static readonly HashSet<string> JsErrorNames = new(StringComparer.Ordinal)
        { "Error", "TypeError", "RangeError", "ReferenceError", "SyntaxError", "EvalError", "URIError", "AggregateError", "InternalError" };

    public static IReadOnlyDictionary<string, object> Server(string exceptionType, string? routeTemplate, string? moduleKey) =>
        Params(("exceptionType", Code(ShortName(exceptionType))), ("route", RouteTemplate(routeTemplate)), ("moduleKey", Code(moduleKey)));

    public static IReadOnlyDictionary<string, object> Client(string? errorName, string? route, string? moduleKey) =>
        Params(("errorName", errorName is not null && JsErrorNames.Contains(errorName) ? errorName : "Error"),
            ("route", RouteTemplate(route)), ("moduleKey", Code(moduleKey)));

    public static IReadOnlyDictionary<string, object> Health(string status, long? value, long? warn, long? critical, int abnormalInstances) =>
        Params(("status", status), ("value", value), ("warn", warn), ("critical", critical), ("abnormalInstances", (long)abnormalInstances));

    public static IReadOnlyDictionary<string, object> Admin(bool granted) => Params(("change", granted ? "granted" : "revoked"));

    public static string? ToJson(IReadOnlyDictionary<string, object>? p) => p is null or { Count: 0 } ? null : JsonSerializer.Serialize(p);

    /// <summary>讀回存在事件列上的參數（數字回 long，其他回字串）。</summary>
    public static IReadOnlyDictionary<string, object>? FromJson(string? json)
    {
        if (json is null) return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(e => e.Name,
            e => e.Value.ValueKind == JsonValueKind.Number ? (object)e.Value.GetInt64() : e.Value.GetString() ?? "", StringComparer.Ordinal);
    }

    /// <summary>繁中標題（通知 TitleSnapshot 的退路，截斷 200 字）。</summary>
    public static string ZhTw(string titleKey, IReadOnlyDictionary<string, object>? p)
    {
        string P(string k) => p is not null && p.TryGetValue(k, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";
        var text = titleKey switch
        {
            ServerError => $"伺服器錯誤：{P("exceptionType")}" + (P("route") is { Length: > 0 } r ? $"（{r}）" : ""),
            ClientError => $"前端錯誤：{P("errorName")}" + (P("route") is { Length: > 0 } r ? $"（{r}）" : ""),
            AdminChanged => P("change") == "granted" ? "系統管理員權限異動：授予" : "系統管理員權限異動：拿掉",
            _ when titleKey.StartsWith(HealthPrefix, StringComparison.Ordinal) =>
                $"健康檢查「{HealthCheckCatalog.Find(titleKey[HealthPrefix.Length..])?.ZhName ?? titleKey[HealthPrefix.Length..]}」{StatusZh(P("status"))}",
            _ => titleKey,
        };
        return text.Length <= 200 ? text : text[..200];
    }

    private static string StatusZh(string status) => status switch
    {
        "warning" => "警告", "critical" => "嚴重", "error" => "檢查本身失敗", "ok" => "正常", _ => status,
    };

    private static IReadOnlyDictionary<string, object> Params(params (string Key, object? Value)[] items) =>
        items.Where(i => i.Value is not null and not "").ToDictionary(i => i.Key, i => i.Value!, StringComparer.Ordinal);

    public static string ShortName(string typeName)
    {
        var plain = typeName.Split('`')[0];
        return plain[(plain.LastIndexOfAny(['.', '+']) + 1)..];
    }

    /// <summary>代碼只收字母、數字、點、底線、連字號（不接受任意文字）。</summary>
    private static string? Code(string? value) => value is not null && CodeRegex().IsMatch(value) ? value : null;

    /// <summary>路由樣板：數字、GUID、長十六進位的段落換成 <c>{id}</c>，其餘段落只留代碼字元；前端回報的是實際路徑，這裡轉成樣板。</summary>
    public static string? RouteTemplate(string? route)
    {
        if (string.IsNullOrEmpty(route)) return null;
        var segments = route.Split('/').Select(s =>
            s.Length == 0 || (s.StartsWith('{') && s.EndsWith('}')) ? s
            : IdSegmentRegex().IsMatch(s) ? "{id}"
            : CodeRegex().IsMatch(s) ? s : "{x}");
        var t = string.Join('/', segments);
        return t.Length <= 200 ? t : t[..200];
    }

    [GeneratedRegex(@"^[A-Za-z0-9._:-]{1,100}$")] private static partial Regex CodeRegex();
    [GeneratedRegex(@"^(\d+|[0-9a-fA-F]{8}-[0-9a-fA-F-]{27}|[0-9a-fA-F]{16,})$")] private static partial Regex IdSegmentRegex();
}
