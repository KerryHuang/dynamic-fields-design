using System.Globalization;
using System.Text;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Application.Dynamic.OpsService.Monitor;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.OpsService.Alerts;

/// <summary>摘要信的一行來源（掛上的佇列列；事件已被清掉時只剩佇列列的主旨）。</summary>
public sealed record DigestItem(long? IncidentId, IncidentSeverity Severity, IncidentCategory? Category, string Title, DateTime CreatedUtc);

/// <summary>
/// 組告警信（維運設計 §17.2.6「信件內容」「長度預算」）：只用標題代碼與白名單參數組標題，不放正規化訊息、原始訊息、stack、記錄值（C2）。
/// 語系用通道的 <c>MailLocale</c>（O2-U14），寫入佇列時組好存成快照。
/// </summary>
public static class AlertMailComposer
{
    // 長度預算（技術限制，§17.9）
    public const int TitleMaxInBody = 500, DigestLineMax = 70, DigestMaxLines = 50, DigestHeadMax = 200, DigestTailReserve = 200;

    /// <summary>一般告警信：主旨「[Formwright][嚴重] 標題（#編號）」；內文類別、處理者、狀況、次數、首次與最近時間、版本、事件連結。</summary>
    public static AlertMail Incident(OpsIncident i, AlertTransition transition, string? mailLocale, string defaultLocale, string edition, AlertMailOptions options)
    {
        var locale = AlertMailText.Locale(mailLocale, defaultLocale);
        var title = IncidentTitles.Text(locale, i.TitleKey, IncidentTitles.FromJson(i.TitleParamsJson));
        var severity = AlertMailText.Label(locale, "severity." + Camel(i.Severity));
        var subject = FitSubject(t => AlertMailText.Get(locale, AlertMailText.Subject, severity, t, i.Id), title);
        var body = new StringBuilder()
            .AppendLine($"[{severity}] {Cut(title, TitleMaxInBody)}")
            .AppendLine()
            .AppendLine(AlertMailText.Get(locale, AlertMailText.Category, AlertMailText.Label(locale, "category." + Camel(i.Category))))
            .AppendLine(AlertMailText.Get(locale, AlertMailText.Handler, AlertMailText.Label(locale, "handler." + OpsMonitorReader.HandlerOf(i.Category, edition))))
            .AppendLine(AlertMailText.Get(locale, AlertMailText.Transition, AlertMailText.Label(locale, "transition." + Camel(transition))))
            .AppendLine(AlertMailText.Get(locale, AlertMailText.Count, i.OccurrenceCount))
            .AppendLine(AlertMailText.Get(locale, AlertMailText.FirstSeen, Time(i.FirstSeenUtc)))
            .AppendLine(AlertMailText.Get(locale, AlertMailText.LastSeen, Time(i.LastSeenUtc)))
            .AppendLine(AlertMailText.Get(locale, AlertMailText.Version, i.LastVersion));
        if (options.Link(OpsNotifier.IncidentLink(i.Id)) is { } link) body.AppendLine(AlertMailText.Get(locale, AlertMailText.Link, link));
        return new AlertMail(subject, FitBody(body.ToString().TrimEnd(), locale));
    }

    /// <summary>
    /// 直送信（資料庫寫不進時，§17.2.6）：還沒有事件編號，主旨不帶編號；內文註明這是看門狗直送、連到監控頁的健康分頁。
    /// </summary>
    public static AlertMail Direct(string titleKey, IReadOnlyDictionary<string, object>? titleParams, IncidentSeverity severityValue,
        IncidentCategory category, AlertTransition transition, DateTime checkedUtc, string version, string? mailLocale, string defaultLocale,
        string edition, AlertMailOptions options)
    {
        var locale = AlertMailText.Locale(mailLocale, defaultLocale);
        var title = IncidentTitles.Text(locale, titleKey, titleParams);
        var severity = AlertMailText.Label(locale, "severity." + Camel(severityValue));
        var subject = FitSubject(t => AlertMailText.Get(locale, AlertMailText.SubjectDirect, severity, t), title);
        var body = new StringBuilder()
            .AppendLine($"[{severity}] {Cut(title, TitleMaxInBody)}")
            .AppendLine()
            .AppendLine(AlertMailText.Get(locale, AlertMailText.DirectNote))
            .AppendLine()
            .AppendLine(AlertMailText.Get(locale, AlertMailText.Category, AlertMailText.Label(locale, "category." + Camel(category))))
            .AppendLine(AlertMailText.Get(locale, AlertMailText.Handler, AlertMailText.Label(locale, "handler." + OpsMonitorReader.HandlerOf(category, edition))))
            .AppendLine(AlertMailText.Get(locale, AlertMailText.Transition, AlertMailText.Label(locale, "transition." + Camel(transition))))
            .AppendLine(AlertMailText.Get(locale, AlertMailText.CheckedAt, Time(checkedUtc)))
            .AppendLine(AlertMailText.Get(locale, AlertMailText.Version, version));
        if (options.Link("/system/ops?tab=health") is { } link) body.AppendLine(AlertMailText.Get(locale, AlertMailText.Link, link));
        return new AlertMail(subject, FitBody(body.ToString().TrimEnd(), locale));
    }

    /// <summary>
    /// 摘要信：「過去一小時另有 N 則告警」，依嚴重度再依時間排列，每行最多 70 字、最多 50 行；
    /// 下一行會讓內文超過 4000 − 200 字就停，結尾「另有 M 則」（M 為 0 時不寫）。
    /// </summary>
    public static AlertMail Digest(IReadOnlyList<DigestItem> items, string? mailLocale, string defaultLocale)
    {
        var locale = AlertMailText.Locale(mailLocale, defaultLocale);
        var subject = AlertMailText.Get(locale, AlertMailText.DigestSubject, items.Count);
        var body = new StringBuilder(Cut(AlertMailText.Get(locale, AlertMailText.DigestIntro, items.Count), DigestHeadMax)).AppendLine();
        var listed = 0;
        foreach (var item in items.OrderByDescending(x => x.Severity).ThenBy(x => x.CreatedUtc))
        {
            if (listed == DigestMaxLines) break;
            var line = Line(item, locale);
            if (body.Length + line.Length + Environment.NewLine.Length > AlertOutbox.BodyMax - DigestTailReserve) break;
            body.AppendLine(line);
            listed++;
        }
        var more = items.Count - listed;
        if (more > 0) body.AppendLine().Append(AlertMailText.Get(locale, AlertMailText.DigestMore, more));
        return new AlertMail(subject, FitBody(body.ToString().TrimEnd(), locale));
    }

    public static AlertMail Test(string? mailLocale, string defaultLocale)
    {
        var locale = AlertMailText.Locale(mailLocale, defaultLocale);
        return new AlertMail(AlertMailText.Get(locale, AlertMailText.TestSubject), AlertMailText.Get(locale, AlertMailText.TestBody));
    }

    /// <summary>寄信設定的測試信（寄到寄件地址本身）。</summary>
    public static AlertMail MailTest(string? locale, string defaultLocale)
    {
        var l = AlertMailText.Locale(locale, defaultLocale);
        return new AlertMail(AlertMailText.Get(l, AlertMailText.MailTestSubject), AlertMailText.Get(l, AlertMailText.MailTestBody));
    }

    /// <summary>摘要信一行：事件編號、嚴重度、類別與標題，超過 70 字截斷加「…」。</summary>
    private static string Line(DigestItem item, string locale)
    {
        var category = item.Category is { } c ? AlertMailText.Label(locale, "category." + Camel(c)) : "-";
        var line = AlertMailText.Get(locale, AlertMailText.DigestLine, item.IncidentId?.ToString(CultureInfo.InvariantCulture) ?? "-",
            AlertMailText.Label(locale, "severity." + Camel(item.Severity)), category, item.Title);
        return line.Length <= DigestLineMax ? line : line[..(DigestLineMax - 1)] + "…";
    }

    /// <summary>主旨超過 200 字時截斷標題並加「…」。</summary>
    private static string FitSubject(Func<string, string> format, string title)
    {
        var subject = format(title);
        if (subject.Length <= AlertOutbox.SubjectMax) return subject;
        var keep = Math.Max(0, title.Length - (subject.Length - AlertOutbox.SubjectMax) - 1);
        subject = format(title[..keep] + "…");
        return subject.Length <= AlertOutbox.SubjectMax ? subject : subject[..AlertOutbox.SubjectMax];
    }

    /// <summary>組字後仍超過 4000 字就截斷並在結尾加「…（內容過長，請看監控頁）」。</summary>
    private static string FitBody(string body, string locale)
    {
        if (body.Length <= AlertOutbox.BodyMax) return body;
        var tail = AlertMailText.Get(locale, AlertMailText.TooLong);
        return body[..(AlertOutbox.BodyMax - tail.Length)] + tail;
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static string Time(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    private static string Camel<T>(T value) where T : struct, Enum => OpsMonitorReader.Camel(value);
}
