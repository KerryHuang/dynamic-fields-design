using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DynamicModules.ApplicationTests.Common;

/// <summary><see cref="MessageCatalog"/>：退回鏈、已安裝語系、一份快照（語系包 i4c 計畫判斷 1、2、4、12、14）。純記憶體。</summary>
public class MessageCatalogTests
{
    private sealed class Header(string locale) : ICurrentLocale { public string Locale => locale; }

    /// <summary>一組記憶體裡的語系快照、政策與 catalog。</summary>
    internal sealed record Env(FakeLocaleRepository Repo, LocaleRegistry Registry, CachedSystemPolicy Policy, MessageCatalog Catalog)
    {
        public void SetDefault(string locale) => Policy.Apply(PolicyCatalog.LocaleDefault, locale, Policy.NextSequence());

        public void SetEnabled(string csv) => Policy.Apply(PolicyCatalog.LocaleEnabled, csv, Policy.NextSequence());

        public async Task RefreshAsync()
        {
            Registry.Invalidate();
            await Registry.EnsureFreshAsync(default);
        }

        /// <summary>請求語系 <paramref name="locale"/> 時 <see cref="RequestLocale"/> 組出的語系與鏈。</summary>
        public LocaleContext Lc(string locale) => new RequestLocale(new Header(locale), Policy).Current;
    }

    internal static async Task<Env> CreateAsync(string defaultLocale = "zh-TW", Action<FakeLocaleRepository>? setup = null)
    {
        var repo = new FakeLocaleRepository().WithBuiltin();
        setup?.Invoke(repo);
        var registry = repo.Registry(TimeProvider.System);
        await registry.EnsureFreshAsync(default);
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var policy = new CachedSystemPolicy(scopes, TimeProvider.System, PolicyEnvironmentDefaults.Empty, registry, NullLogger<CachedSystemPolicy>.Instance);
        var env = new Env(repo, registry, policy, new MessageCatalog(registry, policy));
        env.SetDefault(defaultLocale);
        return env;
    }

    private static readonly Dictionary<string, Dictionary<string, string>> Base = BuiltinServerTextTests.Baseline("message-report.json");

    private static readonly JsonElement Cases =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(BuiltinServerTextTests.RepoRoot(), "shared", "locale-chain-cases.json"))).RootElement.GetProperty("cases");

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases.EnumerateArray()) data.Add(c.GetProperty("name").GetString()!);
        return data;
    }

    private static JsonElement Case(string name) => Cases.EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    private static string[] Strings(JsonElement e) => e.EnumerateArray().Select(x => x.GetString()!).ToArray();

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void SharedChainCases_Walk(string name)
    {
        var c = Case(name);
        var text = c.GetProperty("text").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        var chain = Strings(c.GetProperty("fallbacks")).Prepend(c.GetProperty("locale").GetString()!);

        (MessageCatalog.FirstInChain(chain, l => text.GetValueOrDefault(l)) ?? "").Should().Be(c.GetProperty("expected").GetString(), name);
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task SharedChainCases_CatalogEntry(string name)
    {
        var c = Case(name);
        var env = await CreateAsync(c.GetProperty("default").GetString()!, repo =>
        {
            foreach (var i in c.GetProperty("installed").EnumerateArray())
            {
                var code = i.GetProperty("code").GetString()!;
                if (Domain.Localization.BuiltinLocales.Contains(code)) continue;
                var fb = i.GetProperty("fallback");
                repo.Add(code, code, fallback: fb.ValueKind == JsonValueKind.Null ? null : fb.GetString());
            }
        });
        var locale = c.GetProperty("locale").GetString()!;
        var fallbacks = Strings(c.GetProperty("fallbacks"));

        env.Policy.FallbacksFor(locale).Should().Equal(fallbacks, name);
        var expected = MessageCatalog.FirstInChain([locale, .. fallbacks, "zh-TW"], l => Base[MessageText.Forbidden].GetValueOrDefault(l));
        env.Catalog.Get(new LocaleContext(locale, env.Policy.FallbacksFor(locale)), MessageText.Forbidden).Should().Be(expected, name);
    }

    private static object[] FakeArgs(string template) =>
        Regex.Matches(template, @"\{(\d+)\}").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).DefaultIfEmpty(-1).Max() is var max and >= 0
            ? Enumerable.Range(0, max + 1).Select(i => (object)$"<a{i}>").ToArray()
            : [];

    [Fact]
    public async Task Builtin_SameAsBaseline()
    {
        var env = await CreateAsync();
        foreach (var (key, texts) in Base)
            foreach (var l in BuiltinServerTextTests.Locales)
            {
                var lc = env.Lc(l);
                env.Catalog.Get(lc, key).Should().Be(texts[l], $"{key} 的 {l}");
                var args = FakeArgs(texts["zh-TW"]);
                if (args.Length > 0)
                    env.Catalog.Get(lc, key, args).Should().Be(string.Format(CultureInfo.InvariantCulture, texts[l], args), $"{key} 的 {l} 帶參數");
            }
    }

    [Fact]
    public async Task Ja_ParentThenDefault()
    {
        var withParent = await CreateAsync("th", r => r.Add("ja", "日本語", fallback: "en-US"));
        withParent.Catalog.Get(withParent.Lc("ja"), MessageText.Forbidden).Should().Be(Base[MessageText.Forbidden]["en-US"]);

        var noParent = await CreateAsync("th", r => r.Add("ja", "日本語"));
        noParent.Catalog.Get(noParent.Lc("ja"), MessageText.Forbidden).Should().Be(Base[MessageText.Forbidden]["th"]);
    }

    [Fact]
    public async Task Ja_NotInstalled_StillChains()
    {
        var env = await CreateAsync();
        env.Registry.Codes.Should().NotContain("ja");
        env.Catalog.Get(new LocaleContext("ja", ["en-US"]), MessageText.Forbidden).Should().Be(Base[MessageText.Forbidden]["en-US"]);
        env.Catalog.Get(new LocaleContext("ja", []), MessageText.Forbidden).Should().Be(Base[MessageText.Forbidden]["zh-TW"]);
    }

    [Fact]
    public async Task UnknownKey_Throws()
    {
        var env = await CreateAsync();
        var act = () => env.Catalog.Get(env.Lc("en-US"), "NoSuchKey");
        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public async Task AsI18n_InstalledLocales()
    {
        var env = await CreateAsync();
        var factory = env.Catalog.AsI18n(MessageText.Forbidden);
        factory.Values.Should().BeEquivalentTo(Base[MessageText.Forbidden]);

        env.Repo.Add("ja", "日本語", fallback: "en-US");
        await env.RefreshAsync();
        var five = env.Catalog.AsI18n(MessageText.Forbidden);
        five.Values.Keys.Should().BeEquivalentTo(["zh-TW", "en-US", "th", "vi", "ja"]);
        five.Values["ja"].Should().Be(Base[MessageText.Forbidden]["en-US"]);

        env.SetEnabled("zh-TW,en-US,th,vi");
        env.Catalog.AsI18n(MessageText.Forbidden).Values.Should().ContainKey("ja", "已安裝即產生（M3），停用不影響");
    }

    [Fact]
    public async Task AsI18n_RefreshMidway_UsesOneSnapshot()
    {
        var env = await CreateAsync("zh-TW", r => r.Add("ja", "日本語", fallback: "en-US").Add("xx", "xx"));
        var hooked = false;
        env.Catalog.BeforeLocaleForTests = _ =>
        {
            if (hooked) return;
            hooked = true;
            env.Repo.Remove("ja");
            env.Repo.Add("ja", "日本語", fallback: "th").Add("ko", "한국어");
            env.SetDefault("vi");
            // 掛鉤是同步的 Action；假表的讀取同步完成，不會卡住
#pragma warning disable xUnit1031
            env.RefreshAsync().GetAwaiter().GetResult();
#pragma warning restore xUnit1031
        };

        var first = env.Catalog.AsI18n(MessageText.Forbidden);
        env.Catalog.BeforeLocaleForTests = null;
        var second = env.Catalog.AsI18n(MessageText.Forbidden);

        hooked.Should().BeTrue();
        first.Values.Keys.Should().BeEquivalentTo(["zh-TW", "en-US", "th", "vi", "ja", "xx"]);
        first.Values["ja"].Should().Be(Base[MessageText.Forbidden]["en-US"]);
        first.Values["xx"].Should().Be(Base[MessageText.Forbidden]["zh-TW"], "預設語系整批只讀一次（刷新前是 zh-TW）");
        second.Values.Keys.Should().Contain("ko");
        second.Values["ja"].Should().Be(Base[MessageText.Forbidden]["th"]);
        second.Values["xx"].Should().Be(Base[MessageText.Forbidden]["vi"]);

        foreach (var lc in env.Registry.ContextsFor("vi"))
            lc.Fallbacks.Should().Equal(env.Registry.FallbacksFor(lc.Locale, "vi"), lc.Locale);
        env.Registry.ContextsFor("vi").Select(lc => lc.Locale).Should().Equal(env.Registry.Codes);
    }

    [Fact]
    public async Task AsI18n_Builtin_SameAsBefore()
    {
        // 刪掉的靜態 AsI18n 在 main 的結果＝基準檔四語範本各自帶參數
        var env = await CreateAsync();
        I18nText Expected(string key, params object[] args) => new(BuiltinServerTextTests.Locales.Select(l =>
            KeyValuePair.Create(l, args.Length == 0 ? Base[key][l] : string.Format(CultureInfo.InvariantCulture, Base[key][l], args))));

        ApprovalMessages.FallbackStageName(env.Catalog).Values.Should().Equal(Expected(MessageText.ApprovalFallbackStage).Values);
        ApprovalMessages.PickLabelI18n(env.Catalog).Values.Should().Equal(Expected(MessageText.ApprovalPickLabel).Values);
        ApprovalMessages.AddSignStageName(env.Catalog).Values.Should().Equal(Expected(MessageText.ApprovalAddSignStage).Values);
        ApprovalMessages.ManagerLabelI18n(env.Catalog, 2).Values.Should().Equal(Expected(MessageText.ApprovalManagerLabel, 2).Values);
    }

    [Fact]
    public async Task ApprovalLabels_FollowInstalled()
    {
        var env = await CreateAsync("zh-TW", r => r.Add("ja", "日本語", fallback: "en-US"));
        ApprovalMessages.FallbackStageName(env.Catalog).Values.Should().ContainKey("ja");

        env.Repo.Remove("ja");
        await env.RefreshAsync();
        ApprovalMessages.FallbackStageName(env.Catalog).Values.Should().NotContainKey("ja", "每次呼叫都依當下已安裝的語系產生");
    }

    [Fact]
    public async Task BuiltinInstance_IgnoresRegistry()
    {
        await CreateAsync("th", r => r.Add("ja", "日本語", fallback: "en-US"));
        var b = BuiltinMessageCatalog.Instance;

        b.AsI18n(MessageText.Forbidden).Values.Should().BeEquivalentTo(Base[MessageText.Forbidden]);
        b.Get(new LocaleContext("ja", []), MessageText.Forbidden).Should().Be(Base[MessageText.Forbidden]["zh-TW"]);
        b.Get(new LocaleContext("ja", ["en-US"]), MessageText.Forbidden).Should().Be(Base[MessageText.Forbidden]["en-US"]);
    }
}
