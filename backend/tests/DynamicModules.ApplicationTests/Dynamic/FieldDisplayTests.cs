using System.Text.Json;
using DynamicModules.Application.Dynamic.Display;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>第 7 階段決策 4：各欄位型別的顯示文字。</summary>
public class FieldDisplayTests
{
    private static readonly IReadOnlyDictionary<string, string> NoUsers = new Dictionary<string, string>();

    private static Field F(FieldType type, FieldConfig? config = null, params FieldOption[] options) =>
        new() { Key = "f", Type = type, Config = config ?? new FieldConfig(), Options = [.. options] };

    private static JsonElement J(object? v) => JsonSerializer.SerializeToElement(v);

    private static FieldOption Opt(long id, string zh, string? en = null, int seq = 0, bool active = true)
    {
        var label = new I18nText();
        label.Set("zh-TW", zh);
        if (en is not null) label.Set("en-US", en);
        return new FieldOption { Id = id, LabelI18n = label, Sequence = seq, Active = active };
    }

    private static readonly TimeSpan Taipei = TimeSpan.FromHours(8);

    private static string Fmt(Field f, object? v, string locale = "zh-TW", ReferenceInfo? r = null,
        IReadOnlyDictionary<string, string>? users = null, TimeSpan? offset = null) =>
        FieldDisplay.Format(f, v is null ? null : J(v), new FieldDisplayContext(locale, offset ?? Taipei), r, users ?? NoUsers);

    [Fact]
    public void Option_UsesLocaleFallbacks()
    {
        // 選項只有繁中與英文，要求 th、預設語系 en-US（政策快照組的退回鏈）→ 英文；沒給退回鏈時退回繁中
        var f = F(FieldType.Enum, null, Opt(1, "高", "High"));
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var cache = new CachedSystemPolicy(scopes, TimeProvider.System, PolicyEnvironmentDefaults.Empty,
            new LocaleRegistry(scopes, TimeProvider.System, NullLogger<LocaleRegistry>.Instance), NullLogger<CachedSystemPolicy>.Instance);
        cache.Apply(PolicyCatalog.LocaleDefault, "en-US", cache.NextSequence());
        FieldDisplay.Format(f, J("opt_1"), cache.Display("th"), null, NoUsers).Should().Be("High");
        FieldDisplay.Format(f, J("opt_1"), new FieldDisplayContext("th", Taipei), null, NoUsers).Should().Be("高");
    }

    [Fact]
    public void Null_IsEmpty() => FieldDisplay.Format(F(FieldType.Text), null, new FieldDisplayContext("zh-TW", Taipei), null, NoUsers).Should().Be("");

    [Theory]
    [InlineData(FieldType.Text)]
    [InlineData(FieldType.Textarea)]
    [InlineData(FieldType.Email)]
    [InlineData(FieldType.Phone)]
    [InlineData(FieldType.Url)]
    [InlineData(FieldType.Time)]
    [InlineData(FieldType.AutoNumber)]
    public void Strings_AreAsIs(FieldType type) => Fmt(F(type), "abc").Should().Be("abc");

    [Fact]
    public void Decimal_FixedScale() =>
        Fmt(F(FieldType.Decimal, new FieldConfig { Scale = 2 }), 1234.5m).Should().Be("1234.50");

    [Fact]
    public void Integer_NoDecimals() => Fmt(F(FieldType.Integer), 3).Should().Be("3");

    [Fact]
    public void Currency_Scale0() =>
        Fmt(F(FieldType.Currency, new FieldConfig { Scale = 0 }), 1500).Should().Be("1500");

    [Fact]
    public void Percent_AddsSign() =>
        Fmt(F(FieldType.Percent, new FieldConfig { Scale = 0 }), 5).Should().Be("5%");

    [Fact]
    public void Date_AsIs() => Fmt(F(FieldType.Date), "2026-11-04").Should().Be("2026-11-04");

    [Fact]
    public void DateTime_ConvertedToBusinessTime() =>
        Fmt(F(FieldType.DateTime), "2026-11-04T01:30:00Z").Should().Be("2026-11-04 09:30");

    [Fact]
    public void DateTime_NoOffset_IsTaipeiWallClock() =>
        Fmt(F(FieldType.DateTime), "2026-11-04T23:00:00").Should().Be("2026-11-04 23:00");

    [Fact]
    public void DateTime_UsesGivenOffset() =>
        Fmt(F(FieldType.DateTime), "2026-11-11T10:00:00+08:00", offset: TimeSpan.FromHours(7)).Should().Be("2026-11-11 09:00");

    [Fact]
    public void DateTime_ShiftOverflow_KeepsOriginalOffset()
    {
        Fmt(F(FieldType.DateTime), "9999-12-31T23:30:00+08:00", offset: TimeSpan.FromHours(9)).Should().Be("9999-12-31 23:30 +08:00");
        Fmt(F(FieldType.DateTime), "0001-01-01T08:30:00+08:00", offset: TimeSpan.FromHours(-12)).Should().Be("0001-01-01 08:30 +08:00");
    }

    [Fact]
    public void DateTime_StoredFormat() =>
        Fmt(F(FieldType.DateTime), "2026-11-04T09:30:00+08:00").Should().Be("2026-11-04 09:30");

    [Theory]
    [InlineData("zh-TW", true, "是")]
    [InlineData("zh-TW", false, "否")]
    [InlineData("en-US", true, "Yes")]
    [InlineData("en-US", false, "No")]
    [InlineData("th", true, "ใช่")]
    [InlineData("vi", false, "Không")]
    public void Boolean_ByLocale(string locale, bool v, string expected) =>
        Fmt(F(FieldType.Boolean), v, locale).Should().Be(expected);

    [Fact]
    public void Enum_UsesLabel_IncludingInactive_AndLocale()
    {
        var f = F(FieldType.Enum, null, Opt(3, "進行中", "Open"), Opt(4, "舊", "Old", active: false));
        Fmt(f, "opt_3").Should().Be("進行中");
        Fmt(f, "opt_3", "en-US").Should().Be("Open");
        Fmt(f, "opt_4").Should().Be("舊");
    }

    [Fact]
    public void Enum_FallsBackToZhTw_WhenLocaleLabelMissing() =>
        Fmt(F(FieldType.Enum, null, Opt(3, "進行中")), "opt_3", "en-US").Should().Be("進行中");

    [Fact]
    public void Enum_UnknownId_ReturnsRaw() =>
        Fmt(F(FieldType.Enum, null, Opt(3, "進行中")), "opt_99").Should().Be("opt_99");

    [Fact]
    public void MultiEnum_OrderedByOptionSequence() =>
        Fmt(F(FieldType.MultiEnum, null, Opt(3, "甲", seq: 1), Opt(7, "乙", seq: 2)), new[] { "opt_7", "opt_3" })
            .Should().Be("甲; 乙");

    [Fact]
    public void User_UsesName_OrRaw()
    {
        var users = new Dictionary<string, string> { ["sales1"] = "王小明" };
        Fmt(F(FieldType.User), "sales1", users: users).Should().Be("王小明");
        Fmt(F(FieldType.User), "nobody", users: users).Should().Be("nobody");
    }

    [Fact]
    public void Reference_ByState()
    {
        var f = F(FieldType.Reference);
        Fmt(f, "K1", r: new ReferenceInfo("K1", "客戶A", ReferenceInfo.Ok)).Should().Be("客戶A");
        Fmt(f, "K1", r: new ReferenceInfo("K1", null, ReferenceInfo.Restricted)).Should().Be("（無權限）");
        Fmt(f, "K1", r: new ReferenceInfo("K1", null, ReferenceInfo.Missing)).Should().Be("（已刪除）");
        Fmt(f, "K1", "en-US", new ReferenceInfo("K1", null, ReferenceInfo.Restricted)).Should().Be("(No permission)");
        Fmt(f, "K1", "en-US", new ReferenceInfo("K1", null, ReferenceInfo.Missing)).Should().Be("(Deleted)");
        Fmt(f, "K1", "th", new ReferenceInfo("K1", null, ReferenceInfo.Restricted)).Should().Be("(ไม่มีสิทธิ์)");
        Fmt(f, "K1", "vi", new ReferenceInfo("K1", null, ReferenceInfo.Missing)).Should().Be("(Đã xóa)");
        Fmt(f, "K1").Should().Be("K1");
    }

    // 語系包 i4c 計畫 Task 6：是否、參照的系統字經 catalog 沿鏈取；catalog 為 null 用內建 catalog，鏈照傳入的
    [Fact]
    public async Task Boolean_Ja_FollowsChain()
    {
        var catalog = (await Common.MessageCatalogTests.CreateAsync()).Catalog;
        string Show(FieldDisplayContext c) => FieldDisplay.Format(F(FieldType.Boolean), J(true), c, null, NoUsers);
        Show(new FieldDisplayContext("ja", Taipei, ["en-US", "zh-TW"], catalog)).Should().Be("Yes");
        Show(new FieldDisplayContext("ja", Taipei, ["en-US", "zh-TW"])).Should().Be("Yes");
        Show(new FieldDisplayContext("ja", Taipei, ["zh-TW"])).Should().Be("是");
        Show(new FieldDisplayContext("ja", Taipei)).Should().Be("是");
    }

    [Fact]
    public async Task Reference_Deleted_Ja_FollowsChain()
    {
        var catalog = (await Common.MessageCatalogTests.CreateAsync()).Catalog;
        string Show(FieldDisplayContext c, string state) =>
            FieldDisplay.Format(F(FieldType.Reference), J("K1"), c, new ReferenceInfo("K1", null, state), NoUsers);
        Show(new FieldDisplayContext("ja", Taipei, ["en-US", "zh-TW"], catalog), ReferenceInfo.Missing).Should().Be("(Deleted)");
        Show(new FieldDisplayContext("ja", Taipei, ["en-US", "zh-TW"]), ReferenceInfo.Restricted).Should().Be("(No permission)");
        Show(new FieldDisplayContext("ja", Taipei, ["zh-TW"]), ReferenceInfo.Missing).Should().Be("（已刪除）");
        Show(new FieldDisplayContext("ja", Taipei), ReferenceInfo.Restricted).Should().Be("（無權限）");
    }

    [Fact]
    public void Reference_ModuleTargetStoredAsNumber()
    {
        // 動態模組的參照存成 JSON 數字（記錄 Id）
        var f = F(FieldType.Reference);
        Fmt(f, 42L, r: new ReferenceInfo("42", "訂單A", ReferenceInfo.Ok)).Should().Be("訂單A");
        Fmt(f, 42L).Should().Be("42");
    }

    [Fact]
    public void Formula_FollowsResultType()
    {
        Fmt(F(FieldType.Formula, new FieldConfig { ResultType = "currency", Scale = 0 }), 52500).Should().Be("52500");
        Fmt(F(FieldType.Formula, new FieldConfig { ResultType = "number", Scale = 1 }), 2.5).Should().Be("2.5");
        Fmt(F(FieldType.Rollup, new FieldConfig { ResultType = "date" }), "2026-11-04").Should().Be("2026-11-04");
    }

    [Theory]
    [InlineData(FieldType.Integer, null, true)]
    [InlineData(FieldType.Decimal, null, true)]
    [InlineData(FieldType.Currency, null, true)]
    [InlineData(FieldType.Percent, null, false)]
    [InlineData(FieldType.Text, null, false)]
    [InlineData(FieldType.Formula, "number", true)]
    [InlineData(FieldType.Rollup, "currency", true)]
    [InlineData(FieldType.Formula, "date", false)]
    [InlineData(FieldType.Formula, "decimal", false)]
    public void IsNumericCell(FieldType type, string? resultType, bool expected) =>
        FieldDisplay.IsNumericCell(F(type, new FieldConfig { ResultType = resultType })).Should().Be(expected);
}
