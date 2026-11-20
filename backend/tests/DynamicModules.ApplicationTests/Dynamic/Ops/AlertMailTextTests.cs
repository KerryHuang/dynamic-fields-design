using System.Text.Json;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Application.Dynamic.OpsService.Alerts;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.ApplicationTests.Common;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.Ops;

/// <summary>
/// 告警信與事件標題的文字併進 <see cref="IMessageCatalog"/>（語系包 i4c 計畫 Task 7、Q1）：內建四語與改動前的快照逐字相同，
/// 新語系逐句沿退回鏈取，未知代碼照舊回代碼本身。純函式，直接帶記憶體 catalog。
/// </summary>
public class AlertMailTextTests
{
    /// <summary>新版 API；語系代碼經 <paramref name="context"/> 換成信件語系與鏈。</summary>
    private sealed class Current(IMessageCatalog m, Func<string, LocaleContext> context) : IAlertTextApi
    {
        public AlertMail Incident(OpsIncident i, AlertTransition t, string l, string e, AlertMailOptions o) =>
            AlertMailComposer.Incident(i, t, m, context(l), e, o);
        public AlertMail Direct(string k, IReadOnlyDictionary<string, object>? p, IncidentSeverity s, IncidentCategory c, AlertTransition t, DateTime at, string v,
            string l, string e, AlertMailOptions o) => AlertMailComposer.Direct(k, p, s, c, t, at, v, m, context(l), e, o);
        public AlertMail Digest(IReadOnlyList<DigestItem> items, string l) => AlertMailComposer.Digest(items, m, context(l));
        public AlertMail Test(string l) => AlertMailComposer.Test(m, context(l));
        public AlertMail MailTest(string l) => AlertMailComposer.MailTest(m, context(l));
        public string Title(string l, string k, IReadOnlyDictionary<string, object>? p) => IncidentTitles.Text(m, context(l), k, p);
        public string Snapshot(string l, string k, IReadOnlyDictionary<string, object>? p) => IncidentTitles.Snapshot(m, context(l), k, p);
        public string Label(string l, string c) => AlertMailText.Label(m, context(l), c);
        public string CheckName(string l, string c) => AlertMailText.CheckName(m, context(l), c);
    }

    private static readonly IAlertTextApi Builtin = new Current(BuiltinMessageCatalog.Instance, l => new LocaleContext(l, []));

    private static string VerifiedText() => File.ReadAllText(Path.Combine(
        BuiltinServerTextTests.RepoRoot(), "backend", "tests", "DynamicModules.ApplicationTests", "Dynamic", "Ops", "alert-mail.verified.json"));

    private static Dictionary<string, Dictionary<string, string>> Verified() =>
        JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(VerifiedText())!;

    [Fact]
    public void AlertMail_Builtin_SameAsBefore() => AlertMailCases.Snapshot(Builtin).Should().Be(VerifiedText());

    [Fact]
    public void AlertMail_Ja_ParentEnglish()
    {
        // ja（上層 en-US）：主旨、內文、代碼名稱、健康檢查名稱逐句沿鏈取，內建層只有四語，所以每一句都是英文
        var ja = new Current(BuiltinMessageCatalog.Instance, _ => new LocaleContext("ja", ["en-US", "zh-TW"]));
        AlertMailCases.Run(ja, "ja").Should().Equal(Verified()["en-US"]);
    }

    [Fact]
    public async Task AlertMail_Ja_ParentEnglish_RealCatalog()
    {
        var env = await MessageCatalogTests.CreateAsync("th", r => r.Add("ja", "日本語", fallback: "en-US"));
        var ja = new Current(env.Catalog, l => env.Registry.ContextFor(l, env.Policy.DefaultLocale));
        AlertMailCases.Run(ja, "ja").Should().Equal(Verified()["en-US"]);
        // 沒有上層的新語系落到系統預設語系（th）
        var ko = await MessageCatalogTests.CreateAsync("th", r => r.Add("ko", "한국어"));
        AlertMailCases.Run(new Current(ko.Catalog, l => ko.Registry.ContextFor(l, ko.Policy.DefaultLocale)), "ko").Should().Equal(Verified()["th"]);
    }

    [Fact]
    public void UnknownCodes_KeepFallback()
    {
        var verified = Verified();
        foreach (var l in AlertMailCases.Locales)
        {
            var lc = new LocaleContext(l, []);
            AlertMailText.Label(BuiltinMessageCatalog.Instance, lc, "severity.foo").Should().Be("foo").And.Be(verified[l]["label.unknown"]);
            AlertMailText.Label(BuiltinMessageCatalog.Instance, lc, "nodot").Should().Be("nodot");
            AlertMailText.CheckName(BuiltinMessageCatalog.Instance, lc, "foo.bar").Should().Be("foo.bar").And.Be(verified[l]["checkName.unknown"]);
            IncidentTitles.Text(BuiltinMessageCatalog.Instance, lc, "ops.title.somethingNew", null).Should().Be("ops.title.somethingNew");
        }
    }

    [Fact]
    public async Task IncidentSnapshot_UsesSystemDefault()
    {
        // 站內通知的 TitleSnapshot 用系統預設語系（N1）；出廠是繁中，與改動前（o2b）逐字相同
        var env = await MessageCatalogTests.CreateAsync();
        var p = new Dictionary<string, object> { ["change"] = "granted" };
        var systemDefault = env.Registry.ContextFor(env.Policy.DefaultLocale, env.Policy.DefaultLocale);
        IncidentTitles.Snapshot(env.Catalog, systemDefault, IncidentTitles.AdminChanged, p).Should().Be(Verified()["zh-TW"]["snapshot.short"]);
        env.SetDefault("en-US");
        IncidentTitles.Snapshot(env.Catalog, env.Registry.ContextFor(env.Policy.DefaultLocale, env.Policy.DefaultLocale), IncidentTitles.AdminChanged, p)
            .Should().Be(Verified()["en-US"]["snapshot.short"]);
    }
}
