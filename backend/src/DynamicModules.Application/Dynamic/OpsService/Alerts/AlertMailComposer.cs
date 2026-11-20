using System.Globalization;
using System.Text;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Application.Dynamic.OpsService.Monitor;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.OpsService.Alerts;

/// <summary>摘要信的一行來源（掛上的佇列列；事件已被清掉時只剩佇列列的主旨）。</summary>
public sealed record DigestItem(long? IncidentId, IncidentSeverity Severity, IncidentCategory? Category, string Title, DateTime CreatedUtc);

/// <summary>
/// 組告警信（維運設計 §17.2.6「信件內容」「長度預算」）：只用標題代碼與白名單參數組標題，不放正規化訊息、原始訊息、stack、記錄值（C2）。
/// 語系用通道的 <c>MailLocale</c>（O2-U14）與它的退回鏈（呼叫端組好的 <see cref="LocaleContext"/>），每一句經 <see cref="IMessageCatalog"/> 沿鏈取（語系包 i4c Q1）；寫入佇列時組好存成快照。
/// </summary>
public static class AlertMailComposer
{
    // 長度預算（技術限制，§17.9）
    public const int TitleMaxInBody = 500, DigestLineMax = 70, DigestMaxLines = 50, DigestHeadMax = 200, DigestTailReserve = 200;

    /// <summary>一般告警信：主旨「[Formwright][嚴重] 標題（#編號）」；內文類別、處理者、狀況、次數、首次與最近時間、版本、事件連結。</summary>
    public static AlertMail Incident(OpsIncident i, AlertTransition transition, IMessageCatalog m, LocaleContext mail, string edition, AlertMailOptions options)
    {
        var title = IncidentTitles.Text(m, mail, i.TitleKey, IncidentTitles.FromJson(i.TitleParamsJson));
        var severity = AlertMailText.Label(m, mail, "severity." + Camel(i.Severity));
        var subject = FitSubject(t => m.Get(mail, AlertMailText.Subject, severity, t, i.Id), title);
        var body = new StringBuilder()
            .AppendLine($"[{severity}] {Cut(title, TitleMaxInBody)}")
            .AppendLine()
            .AppendLine(m.Get(mail, AlertMailText.Category, AlertMailText.Label(m, mail, "category." + Camel(i.Category))))
            .AppendLine(m.Get(mail, AlertMailText.Handler, AlertMailText.Label(m, mail, "handler." + OpsMonitorReader.HandlerOf(i.Category, edition))))
            .AppendLine(m.Get(mail, AlertMailText.Transition, AlertMailText.Label(m, mail, "transition." + Camel(transition))))
            .AppendLine(m.Get(mail, AlertMailText.Count, i.OccurrenceCount))
            .AppendLine(m.Get(mail, AlertMailText.FirstSeen, Time(i.FirstSeenUtc)))
            .AppendLine(m.Get(mail, AlertMailText.LastSeen, Time(i.LastSeenUtc)))
            .AppendLine(m.Get(mail, AlertMailText.Version, i.LastVersion));
        if (options.Link(OpsNotifier.IncidentLink(i.Id)) is { } link) body.AppendLine(m.Get(mail, AlertMailText.Link, link));
        return new AlertMail(subject, FitBody(body.ToString().TrimEnd(), m, mail));
    }

    /// <summary>
    /// 直送信（資料庫寫不進時，§17.2.6）：還沒有事件編號，主旨不帶編號；內文註明這是看門狗直送、連到監控頁的健康分頁。
    /// </summary>
    public static AlertMail Direct(string titleKey, IReadOnlyDictionary<string, object>? titleParams, IncidentSeverity severityValue,
        IncidentCategory category, AlertTransition transition, DateTime checkedUtc, string version, IMessageCatalog m, LocaleContext mail,
        string edition, AlertMailOptions options)
    {
        var title = IncidentTitles.Text(m, mail, titleKey, titleParams);
        var severity = AlertMailText.Label(m, mail, "severity." + Camel(severityValue));
        var subject = FitSubject(t => m.Get(mail, AlertMailText.SubjectDirect, severity, t), title);
        var body = new StringBuilder()
            .AppendLine($"[{severity}] {Cut(title, TitleMaxInBody)}")
            .AppendLine()
            .AppendLine(m.Get(mail, AlertMailText.DirectNote))
            .AppendLine()
            .AppendLine(m.Get(mail, AlertMailText.Category, AlertMailText.Label(m, mail, "category." + Camel(category))))
            .AppendLine(m.Get(mail, AlertMailText.Handler, AlertMailText.Label(m, mail, "handler." + OpsMonitorReader.HandlerOf(category, edition))))
            .AppendLine(m.Get(mail, AlertMailText.Transition, AlertMailText.Label(m, mail, "transition." + Camel(transition))))
            .AppendLine(m.Get(mail, AlertMailText.CheckedAt, Time(checkedUtc)))
            .AppendLine(m.Get(mail, AlertMailText.Version, version));
        if (options.Link("/system/ops?tab=health") is { } link) body.AppendLine(m.Get(mail, AlertMailText.Link, link));
        return new AlertMail(subject, FitBody(body.ToString().TrimEnd(), m, mail));
    }

    /// <summary>
    /// 摘要信：「過去一小時另有 N 則告警」，依嚴重度再依時間排列，每行最多 70 字、最多 50 行；
    /// 下一行會讓內文超過 4000 − 200 字就停，結尾「另有 M 則」（M 為 0 時不寫）。
    /// </summary>
    public static AlertMail Digest(IReadOnlyList<DigestItem> items, IMessageCatalog m, LocaleContext mail)
    {
        var subject = m.Get(mail, AlertMailText.DigestSubject, items.Count);
        var body = new StringBuilder(Cut(m.Get(mail, AlertMailText.DigestIntro, items.Count), DigestHeadMax)).AppendLine();
        var listed = 0;
        foreach (var item in items.OrderByDescending(x => x.Severity).ThenBy(x => x.CreatedUtc))
        {
            if (listed == DigestMaxLines) break;
            var line = Line(item, m, mail);
            if (body.Length + line.Length + Environment.NewLine.Length > AlertOutbox.BodyMax - DigestTailReserve) break;
            body.AppendLine(line);
            listed++;
        }
        var more = items.Count - listed;
        if (more > 0) body.AppendLine().Append(m.Get(mail, AlertMailText.DigestMore, more));
        return new AlertMail(subject, FitBody(body.ToString().TrimEnd(), m, mail));
    }

    public static AlertMail Test(IMessageCatalog m, LocaleContext mail)
    {
        return new AlertMail(m.Get(mail, AlertMailText.TestSubject), m.Get(mail, AlertMailText.TestBody));
    }

    /// <summary>寄信設定的測試信（寄到寄件地址本身）。</summary>
    public static AlertMail MailTest(IMessageCatalog m, LocaleContext mail)
    {
        return new AlertMail(m.Get(mail, AlertMailText.MailTestSubject), m.Get(mail, AlertMailText.MailTestBody));
    }

    /// <summary>摘要信一行：事件編號、嚴重度、類別與標題，超過 70 字截斷加「…」。</summary>
    private static string Line(DigestItem item, IMessageCatalog m, LocaleContext mail)
    {
        var category = item.Category is { } c ? AlertMailText.Label(m, mail, "category." + Camel(c)) : "-";
        var line = m.Get(mail, AlertMailText.DigestLine, item.IncidentId?.ToString(CultureInfo.InvariantCulture) ?? "-",
            AlertMailText.Label(m, mail, "severity." + Camel(item.Severity)), category, item.Title);
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
    private static string FitBody(string body, IMessageCatalog m, LocaleContext mail)
    {
        if (body.Length <= AlertOutbox.BodyMax) return body;
        var tail = m.Get(mail, AlertMailText.TooLong);
        return body[..(AlertOutbox.BodyMax - tail.Length)] + tail;
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static string Time(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    private static string Camel<T>(T value) where T : struct, Enum => OpsMonitorReader.Camel(value);
}
