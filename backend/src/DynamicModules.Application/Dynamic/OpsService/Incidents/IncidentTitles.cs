using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Application.Dynamic.OpsService.Alerts;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.OpsService.Incidents;

/// <summary>
/// 事件標題的唯一組字處（維運設計 §17.2.2、O2-T22）：各來源只接受白名單參數，值只能是代碼、數字或路由樣板（<c>{id}</c> 形式），
/// 不接受任意字串。畫面與鈴鐺依語系組字；這裡另組文字給站內通知的 <c>TitleSnapshot</c>（繁中，只當退路）與告警信（通道的信件語系，O2-U14）。
/// </summary>
public static partial class IncidentTitles
{
    public const string ServerError = "ops.title.serverError", ClientError = "ops.title.clientError",
        AdminChanged = "ops.title.adminChanged", HealthPrefix = "ops.title.health.", ChannelFailed = "ops.title.channelFailed",
        ConfigPrefix = "ops.title.config.", RulePaused = "ops.title.rulePaused", PrivilegeChanged = "ops.title.privilegeChanged",
        McpWriteLoop = "ops.title.mcpWriteLoop";

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

    /// <summary>通道失敗（§17.2.2 標題表）：失敗階段與 SMTP 回應碼。</summary>
    public static IReadOnlyDictionary<string, object> Channel(string stage, int? serverCode) =>
        Params(("stage", Code(stage)), ("serverCode", serverCode is { } c ? (long)c : null));

    /// <summary>客戶設定類（§17.2.2 標題表）：規則種類、規則代碼、模組代碼、錯誤碼。</summary>
    public static IReadOnlyDictionary<string, object> Config(string ruleKind, string? ruleKey, string? moduleKey, string errorCode) =>
        Params(("ruleKind", Code(ruleKind)), ("ruleKey", Code(ruleKey)), ("moduleKey", Code(moduleKey)), ("errorCode", Code(errorCode)));

    /// <summary>規則自動暫停：另帶門檻與窗口內的失敗次數。</summary>
    public static IReadOnlyDictionary<string, object> Paused(string ruleKind, string? ruleKey, string? moduleKey, string? lastErrorCode, int threshold,
        int failures) =>
        Params(("ruleKind", Code(ruleKind)), ("ruleKey", Code(ruleKey)), ("moduleKey", Code(moduleKey)), ("errorCode", Code(lastErrorCode)),
            ("threshold", (long)threshold), ("failures", (long)failures));

    /// <summary>AI 寫入迴圈（MCP 設計 §20 C3）：只放權杖 id、記錄 id、次數、時間窗分鐘數，權杖名稱是使用者輸入的字串不進標題；權杖 id 對得到「AI 連線」頁權杖卡片上的 #id。</summary>
    public static IReadOnlyDictionary<string, object> WriteLoop(long tokenId, long recordId, int count, int minutes) =>
        Params(("tokenId", tokenId), ("recordId", recordId), ("count", (long)count), ("minutes", (long)minutes));

    public static string? ToJson(IReadOnlyDictionary<string, object>? p) => p is null or { Count: 0 } ? null : JsonSerializer.Serialize(p);

    /// <summary>讀回存在事件列上的參數（數字回 long，其他回字串）。</summary>
    public static IReadOnlyDictionary<string, object>? FromJson(string? json)
    {
        if (json is null) return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(e => e.Name,
            e => e.Value.ValueKind == JsonValueKind.Number ? (object)e.Value.GetInt64() : e.Value.GetString() ?? "", StringComparer.Ordinal);
    }

    /// <summary>系統預設語系的標題（通知 TitleSnapshot 的退路，截斷 200 字）；每一句沿預設語系的退回鏈取，最後是繁中。</summary>
    public static string Snapshot(IMessageCatalog m, LocaleContext systemDefault, string titleKey, IReadOnlyDictionary<string, object>? p)
    {
        var text = Text(m, systemDefault, titleKey, p);
        return text.Length <= 200 ? text : text[..200];
    }

    /// <summary>依語系組標題（不截斷；呼叫端各自有長度預算）。沒登記的標題代碼回代碼本身。</summary>
    public static string Text(IMessageCatalog m, LocaleContext locale, string titleKey, IReadOnlyDictionary<string, object>? p)
    {
        string P(string k) => p is not null && p.TryGetValue(k, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";
        string Route() => P("route") is { Length: > 0 } r ? m.Get(locale, AlertMailText.TitleRouteSuffix, r) : "";
        return titleKey switch
        {
            ServerError => m.Get(locale, AlertMailText.TitleServer, P("exceptionType")) + Route(),
            ClientError => m.Get(locale, AlertMailText.TitleClient, P("errorName")) + Route(),
            AdminChanged => m.Get(locale, P("change") == "granted" ? AlertMailText.TitleAdminGranted : AlertMailText.TitleAdminRevoked),
            ChannelFailed => m.Get(locale, AlertMailText.TitleChannel, AlertMailText.Label(m, locale, "stage." + P("stage"))
                + (P("serverCode") is { Length: > 0 } c ? m.Get(locale, AlertMailText.TitleChannelCode, c) : "")),
            PrivilegeChanged => m.Get(locale, P("change") == "granted" ? AlertMailText.TitlePrivilegeGranted : AlertMailText.TitlePrivilegeRevoked),
            RulePaused => m.Get(locale, AlertMailText.TitleRulePaused, P("ruleKey"), P("failures")),
            McpWriteLoop => m.Get(locale, AlertMailText.TitleMcpWriteLoop, P("tokenId"), P("recordId"), P("count"), P("minutes")),
            _ when titleKey.StartsWith(ConfigPrefix, StringComparison.Ordinal) => m.Get(locale, AlertMailText.TitleConfig, P("ruleKey"),
                AlertMailText.Label(m, locale, "ruleError." + titleKey[ConfigPrefix.Length..])),
            _ when titleKey.StartsWith(HealthPrefix, StringComparison.Ordinal) => m.Get(locale, AlertMailText.TitleHealth,
                AlertMailText.CheckName(m, locale, titleKey[HealthPrefix.Length..]), AlertMailText.Label(m, locale, "status." + P("status"))),
            _ => titleKey,
        };
    }

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
