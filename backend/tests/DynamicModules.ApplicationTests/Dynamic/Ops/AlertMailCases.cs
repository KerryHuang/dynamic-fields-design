using System.Text.Encodings.Web;
using System.Text.Json;
using DynamicModules.Application.Dynamic.OpsService.Alerts;
using DynamicModules.Application.Dynamic.OpsService.Health;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.ApplicationTests.Dynamic.Ops;

/// <summary>告警信與事件標題的入口（以內建語系代碼呼叫），讓同一組案例能跑在不同版本的 API 上。</summary>
internal interface IAlertTextApi
{
    AlertMail Incident(OpsIncident incident, AlertTransition transition, string locale, string edition, AlertMailOptions options);

    AlertMail Direct(string titleKey, IReadOnlyDictionary<string, object>? titleParams, IncidentSeverity severity, IncidentCategory category,
        AlertTransition transition, DateTime checkedUtc, string version, string locale, string edition, AlertMailOptions options);

    AlertMail Digest(IReadOnlyList<DigestItem> items, string locale);

    AlertMail Test(string locale);

    AlertMail MailTest(string locale);

    string Title(string locale, string titleKey, IReadOnlyDictionary<string, object>? titleParams);

    string Snapshot(string locale, string titleKey, IReadOnlyDictionary<string, object>? titleParams);

    string Label(string locale, string code);

    string CheckName(string locale, string code);
}

/// <summary>
/// 告警信、事件標題的輸出快照案例（語系包 i4c 計畫 Task 7 Step 0）：五個信件入口 × 嚴重度、類別、處理者、轉換、連結有無、長度截斷，
/// 事件標題每種代碼與分支，未知代碼的退路。固定時間與編號。
/// </summary>
internal static class AlertMailCases
{
    public static readonly string[] Locales = ["zh-TW", "en-US", "th", "vi"];

    private static readonly DateTime First = new(2026, 11, 1, 8, 30, 0, DateTimeKind.Utc), Last = new(2026, 11, 2, 9, 45, 10, DateTimeKind.Utc);
    private static readonly AlertMailOptions Linked = new("https://formwright.example");
    private static readonly AlertMailOptions NoLink = AlertMailOptions.None;
    private static readonly string[] Editions = ["Cloud", "OnPremise"];
    private static readonly IncidentCategory[] Categories = Enum.GetValues<IncidentCategory>();
    private static readonly IncidentSeverity[] Severities = Enum.GetValues<IncidentSeverity>();
    private static readonly AlertTransition[] Transitions =
        [AlertTransition.Opened, AlertTransition.Regressed, AlertTransition.Escalated, AlertTransition.Renotify, AlertTransition.Occurred];

    private static Dictionary<string, object> P(params (string Key, object Value)[] items) => items.ToDictionary(i => i.Key, i => i.Value, StringComparer.Ordinal);

    /// <summary>事件標題的每種代碼與分支（含未登記的標題代碼）。</summary>
    public static IEnumerable<(string Name, string Key, Dictionary<string, object>? Params)> Titles()
    {
        yield return ("server.route", IncidentTitles.ServerError, P(("exceptionType", "NullReferenceException"), ("route", "/api/v3/records/{id}")));
        yield return ("server.noRoute", IncidentTitles.ServerError, P(("exceptionType", "InvalidOperationException")));
        yield return ("client.route", IncidentTitles.ClientError, P(("errorName", "TypeError"), ("route", "/records/{id}")));
        yield return ("client.noRoute", IncidentTitles.ClientError, P(("errorName", "Error")));
        yield return ("admin.granted", IncidentTitles.AdminChanged, P(("change", "granted")));
        yield return ("admin.revoked", IncidentTitles.AdminChanged, P(("change", "revoked")));
        yield return ("privilege.granted", IncidentTitles.PrivilegeChanged, P(("change", "granted")));
        yield return ("privilege.revoked", IncidentTitles.PrivilegeChanged, P(("change", "revoked")));
        foreach (var stage in new[] { "connect", "tls", "auth", "send", "config", "foo" })
        {
            yield return ($"channel.{stage}.code", IncidentTitles.ChannelFailed, P(("stage", stage), ("serverCode", 535L)));
            yield return ($"channel.{stage}", IncidentTitles.ChannelFailed, P(("stage", stage)));
        }
        yield return ("rulePaused", IncidentTitles.RulePaused, P(("ruleKey", "ship-remind"), ("failures", 5L), ("threshold", 5L)));
        foreach (var error in new[] { "event.tooDeep", "event.loop", "check.conditionInvalid", "effect.targetUnavailable", "effect.missingDetail",
                     "effect.tooManyRows", "effect.handlerMissing", "effect.unknownKind", "effect.foo" })
            yield return ($"config.{error}", IncidentTitles.ConfigPrefix + error, P(("ruleKey", "order-check")));
        foreach (var check in HealthCheckCatalog.All.Select(c => c.Code).Append("foo.bar"))
            foreach (var status in new[] { "ok", "warning", "critical", "error" })
                yield return ($"health.{check}.{status}", IncidentTitles.HealthPrefix + check, P(("status", status), ("value", 90L)));
        yield return ("unknownTitle", "ops.title.somethingNew", null);
    }

    private static OpsIncident Incident(long id, IncidentCategory category, IncidentSeverity severity, string titleKey, IReadOnlyDictionary<string, object>? p,
        string version = "1.4.2") => new()
    {
        Id = id, Category = category, Severity = severity, TitleKey = titleKey, TitleParamsJson = IncidentTitles.ToJson(p),
        OccurrenceCount = id * 3, FirstSeenUtc = First, LastSeenUtc = Last, LastVersion = version,
    };

    private static string Mail(AlertMail m) => m.Subject + "\n---\n" + m.Body;

    /// <summary>一個語系的全部案例輸出（名稱 → 文字）。</summary>
    public static SortedDictionary<string, string> Run(IAlertTextApi api, string l)
    {
        var o = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var titles = Titles().ToList();

        // Incident：類別 × 版本（五種處理者），嚴重度與轉換輪流；連結有無各半
        var n = 0;
        foreach (var category in Categories)
            foreach (var edition in Editions)
            {
                var severity = Severities[n % Severities.Length];
                var transition = Transitions[n % Transitions.Length];
                var (_, key, p) = titles[n % titles.Count];
                o[$"incident.{category}.{edition}.{severity}.{transition}"] =
                    Mail(api.Incident(Incident(100 + n, category, severity, key, p), transition, l, edition, n % 2 == 0 ? Linked : NoLink));
                n++;
            }
        foreach (var transition in Transitions.Append(AlertTransition.Digest))
            o[$"incident.transition.{transition}"] =
                Mail(api.Incident(Incident(200, IncidentCategory.Defect, IncidentSeverity.Critical, IncidentTitles.ServerError, titles[0].Params), transition, l, "Cloud", Linked));
        var longTitle = P(("exceptionType", new string('X', 600)));
        o["incident.longTitle"] = Mail(api.Incident(Incident(300, IncidentCategory.Defect, IncidentSeverity.Warning, IncidentTitles.ServerError, longTitle),
            AlertTransition.Opened, l, "Cloud", Linked));
        o["incident.longBody"] = Mail(api.Incident(Incident(301, IncidentCategory.Infra, IncidentSeverity.Info, IncidentTitles.ServerError, titles[1].Params,
            version: new string('v', 4100)), AlertTransition.Opened, l, "OnPremise", Linked));

        // Direct：同樣的涵蓋
        n = 0;
        foreach (var category in Categories)
            foreach (var edition in Editions)
            {
                var severity = Severities[(n + 1) % Severities.Length];
                var transition = Transitions[(n + 2) % Transitions.Length];
                var check = HealthCheckCatalog.All[n % HealthCheckCatalog.All.Count].Code;
                o[$"direct.{category}.{edition}.{severity}.{transition}"] = Mail(api.Direct(IncidentTitles.HealthPrefix + check,
                    P(("status", "critical"), ("value", 97L)), severity, category, transition, Last, "1.4.2", l, edition, n % 2 == 0 ? NoLink : Linked));
                n++;
            }
        o["direct.longTitle"] = Mail(api.Direct(IncidentTitles.ServerError, longTitle, IncidentSeverity.Critical, IncidentCategory.Infra,
            AlertTransition.Opened, Last, "1.4.2", l, "Cloud", Linked));
        o["direct.longBody"] = Mail(api.Direct(IncidentTitles.ServerError, titles[0].Params, IncidentSeverity.Critical, IncidentCategory.Infra,
            AlertTransition.Opened, Last, new string('v', 4100), l, "Cloud", Linked));

        // Digest：0、1、超過行數、單行過長、類別 null、事件編號 null
        DigestItem Item(long? id, IncidentSeverity s, IncidentCategory? c, string title, int minute) =>
            new(id, s, c, title, First.AddMinutes(minute));
        o["digest.empty"] = Mail(api.Digest([], l));
        o["digest.one"] = Mail(api.Digest([Item(7, IncidentSeverity.Warning, IncidentCategory.Config, "規則設定有問題", 1)], l));
        o["digest.many"] = Mail(api.Digest(Enumerable.Range(0, AlertMailComposer.DigestMaxLines + 5)
            .Select(i => Item(1000 + i, Severities[i % 3], Categories[i % 4], $"Title {i}", i)).ToList(), l));
        o["digest.longLine"] = Mail(api.Digest([Item(8, IncidentSeverity.Critical, IncidentCategory.Infra, new string('長', 120), 2)], l));
        o["digest.noCategory"] = Mail(api.Digest([Item(9, IncidentSeverity.Info, null, "No category", 3)], l));
        o["digest.noIncident"] = Mail(api.Digest([Item(null, IncidentSeverity.Warning, IncidentCategory.Security, "Cleared", 4),
            Item(10, IncidentSeverity.Critical, IncidentCategory.Defect, "Kept", 5)], l));

        o["test"] = Mail(api.Test(l));
        o["mailTest"] = Mail(api.MailTest(l));

        foreach (var (name, key, p) in titles) o["title." + name] = api.Title(l, key, p);
        o["snapshot.short"] = api.Snapshot(l, IncidentTitles.AdminChanged, P(("change", "granted")));
        o["snapshot.long"] = api.Snapshot(l, IncidentTitles.ServerError, longTitle);
        o["label.unknown"] = api.Label(l, "severity.foo");
        o["label.known"] = api.Label(l, "severity.critical");
        o["checkName.unknown"] = api.CheckName(l, "foo.bar");
        o["checkName.known"] = api.CheckName(l, HealthCheckCatalog.All[0].Code);
        return o;
    }

    /// <summary>四語 × 全部案例，JSON（鍵排序、縮排、不跳脫中文）。</summary>
    public static string Snapshot(IAlertTextApi api) =>
        JsonSerializer.Serialize(Locales.ToDictionary(l => l, l => Run(api, l)),
            new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
}
