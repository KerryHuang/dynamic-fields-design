using System.Text.Json;
using DynamicModules.Application.Dynamic.ImportService;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>第 7 階段決策 6：匯入值解析（FieldDisplay 的反向）。</summary>
public class FieldImportParserTests
{
    private static readonly ImportLookups None = new(
        new Dictionary<string, string>(),
        new Dictionary<long, IReadOnlyDictionary<string, IReadOnlyList<string>>>());

    private static Field F(FieldType type, FieldConfig? config = null, params FieldOption[] options) =>
        new() { Id = 7, Key = "f", Type = type, Config = config ?? new FieldConfig(), Options = [.. options] };

    private static FieldOption Opt(long id, string zh, string? en = null, bool active = true)
    {
        var label = new I18nText();
        label.Set("zh-TW", zh);
        if (en is not null) label.Set("en-US", en);
        return new FieldOption { Id = id, LabelI18n = label, Active = active };
    }

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static (string? Raw, string? Error) P(Field f, string text, ImportLookups? l = null)
    {
        var (v, e) = FieldImportParser.Parse(f, text, l ?? None);
        // 字串值用 GetString 再重新序列化為 JSON 字面值，避開序列化器對 + 的 \u002B 跳脫
        return (v is { ValueKind: JsonValueKind.String } sv ? System.Text.Json.JsonSerializer.Serialize(sv.GetString(), Relaxed) : v?.GetRawText(), e);
    }

    [Theory]
    [InlineData(FieldType.Decimal, "1,234.5", "1234.5")]
    [InlineData(FieldType.Integer, "3", "3")]
    [InlineData(FieldType.Currency, " 12.50 ", "12.50")]
    [InlineData(FieldType.Percent, "5%", "5")]
    [InlineData(FieldType.Percent, "5", "5")]
    [InlineData(FieldType.Decimal, "1,234,567.25", "1234567.25")]
    [InlineData(FieldType.Decimal, "-1,234", "-1234")]
    [InlineData(FieldType.Integer, "1234567", "1234567")]
    [InlineData(FieldType.Percent, "1,234.5%", "1234.5")]
    public void Parse_Numbers(FieldType type, string text, string expected) =>
        P(F(type), text).Should().Be((expected, null));

    [Theory]
    [InlineData(FieldType.Decimal, "abc")]
    [InlineData(FieldType.Integer, "1.2.3")]
    [InlineData(FieldType.Decimal, "5%")]
    [InlineData(FieldType.Decimal, "1,5")]
    [InlineData(FieldType.Integer, "1,23")]
    [InlineData(FieldType.Decimal, "12,3456")]
    [InlineData(FieldType.Decimal, "1234,567")]
    [InlineData(FieldType.Decimal, ",123")]
    [InlineData(FieldType.Percent, "1,5%")]
    public void Parse_Numbers_Invalid(FieldType type, string text) =>
        P(F(type), text).Should().Be((null, "請輸入數字"));

    [Theory]
    [InlineData("是", "true")]
    [InlineData("TRUE", "true")]
    [InlineData("Yes", "true")]
    [InlineData("1", "true")]
    [InlineData("否", "false")]
    [InlineData("no", "false")]
    [InlineData("0", "false")]
    public void Parse_Boolean(string text, string expected) =>
        P(F(FieldType.Boolean), text).Should().Be((expected, null));

    [Theory]
    [InlineData("zh-TW")]
    [InlineData("en-US")]
    [InlineData("th")]
    [InlineData("vi")]
    public void Parse_Boolean_accepts_what_each_locale_exports(string locale)
    {
        P(F(FieldType.Boolean), ReportText.Get(locale, ReportText.Yes)).Should().Be(("true", null));
        P(F(FieldType.Boolean), ReportText.Get(locale, ReportText.No)).Should().Be(("false", null));
    }

    [Fact]
    public void Parse_Boolean_Invalid() =>
        P(F(FieldType.Boolean), "maybe").Should().Be((null, "請輸入「是」或「否」"));

    [Theory]
    [InlineData(FieldType.Date, "2026/11/4", "\"2026-11-04\"")]
    [InlineData(FieldType.Date, "2026-11-04", "\"2026-11-04\"")]
    [InlineData(FieldType.Time, "9:30", "\"09:30\"")]
    [InlineData(FieldType.Time, "14:05", "\"14:05\"")]
    [InlineData(FieldType.DateTime, "2026-11-04 09:30", "\"2026-11-04T09:30:00+08:00\"")]
    [InlineData(FieldType.DateTime, "2026-11-04T01:30:00Z", "\"2026-11-04T09:30:00+08:00\"")]
    [InlineData(FieldType.DateTime, "2026-11-04T09:30:00+08:00", "\"2026-11-04T09:30:00+08:00\"")]
    public void Parse_Dates(FieldType type, string text, string expected) =>
        P(F(type), text).Should().Be((expected, null));

    [Theory]
    [InlineData(FieldType.Date, "11/4/2026")]
    [InlineData(FieldType.DateTime, "tomorrow")]
    [InlineData(FieldType.Time, "25:00")]
    public void Parse_Dates_Invalid(FieldType type, string text) =>
        P(F(type), text).Error.Should().NotBeNullOrEmpty();

    [Fact]
    public void Parse_Enum_ByAnyLocaleLabel_CaseInsensitive()
    {
        var f = F(FieldType.Enum, null, Opt(1, "進行中", "In Progress"), Opt(2, "完成", "Done", active: false));
        P(f, "進行中").Should().Be(("\"opt_1\"", null));
        P(f, "in progress").Should().Be(("\"opt_1\"", null));
        P(f, "DONE").Should().Be(("\"opt_2\"", null)); // 停用的選項照樣轉成 id
    }

    [Fact]
    public void Parse_Enum_Ambiguous()
    {
        var f = F(FieldType.Enum, null, Opt(1, "A", "same"), Opt(2, "B", "SAME"));
        P(f, "same").Should().Be((null, "選項「same」不只一個"));
    }

    [Fact]
    public void Parse_Enum_NotFound() =>
        P(F(FieldType.Enum, null, Opt(1, "A")), "Z").Should().Be((null, "找不到選項「Z」"));

    [Fact]
    public void Parse_MultiEnum_SplitsSemicolon()
    {
        var f = F(FieldType.MultiEnum, null, Opt(1, "紅", "Red"), Opt(2, "藍", "Blue"));
        P(f, "紅; blue").Should().Be(("[\"opt_1\",\"opt_2\"]", null));
        P(f, "紅;綠").Should().Be((null, "找不到選項「綠」"));
    }

    [Fact]
    public void Parse_User_ByIdOrName()
    {
        var l = new ImportLookups(new Dictionary<string, string> { ["王小明"] = "u01" },
            new Dictionary<long, IReadOnlyDictionary<string, IReadOnlyList<string>>>());
        var f = F(FieldType.User);
        P(f, "王小明", l).Should().Be(("\"u01\"", null));
        P(f, "u01", l).Should().Be(("\"u01\"", null));
        P(f, "nobody", l).Should().Be((null, "找不到使用者「nobody」"));
    }

    [Fact]
    public void Parse_Reference_UniqueAmbiguousMissing()
    {
        var titles = new Dictionary<string, IReadOnlyList<string>>
        {
            ["甲"] = ["12"], ["乙"] = ["1", "2"],
        };
        var l = new ImportLookups(new Dictionary<string, string>(),
            new Dictionary<long, IReadOnlyDictionary<string, IReadOnlyList<string>>> { [7] = titles });
        var module = F(FieldType.Reference, new FieldConfig { RefTarget = new ReferenceTarget("module", "m") });
        var entity = F(FieldType.Reference, new FieldConfig { RefTarget = new ReferenceTarget("entity", "e") });

        P(module, "甲", l).Should().Be(("12", null));      // 模組參照存數字
        P(entity, "甲", l).Should().Be(("\"12\"", null));  // 實體參照存字串
        P(module, "乙", l).Should().Be((null, "「乙」有多筆同名資料"));
        P(module, "丙", l).Should().Be((null, "找不到「丙」"));
    }

    [Fact]
    public void Parse_Text_StripsInjectionApostrophe()
    {
        P(F(FieldType.Text), "'=1+1").Should().Be(("\"=1+1\"", null));
        P(F(FieldType.Email), "'@x").Should().Be(("\"@x\"", null));
        P(F(FieldType.Text), "'abc").Should().Be(("\"'abc\"", null));
    }

    [Fact]
    public void Parse_LookupTypes_StripInjectionApostrophe()
    {
        var enumF = F(FieldType.Enum, null, Opt(1, "=甲"));
        P(enumF, "'=甲").Should().Be(("\"opt_1\"", null));
        var multi = F(FieldType.MultiEnum, null, Opt(1, "-紅"), Opt(2, "藍"));
        P(multi, "'-紅; 藍").Should().Be(("[\"opt_1\",\"opt_2\"]", null));
        var l = new ImportLookups(new Dictionary<string, string> { ["@小明"] = "u01" },
            new Dictionary<long, IReadOnlyDictionary<string, IReadOnlyList<string>>>
            {
                [7] = new Dictionary<string, IReadOnlyList<string>> { ["+甲"] = ["12"] },
            });
        P(F(FieldType.User), "'@小明", l).Should().Be(("\"u01\"", null));
        P(F(FieldType.Reference, new FieldConfig { RefTarget = new ReferenceTarget("module", "m") }), "'+甲", l)
            .Should().Be(("12", null));
    }

    [Theory]
    [InlineData(FieldType.Text)]
    [InlineData(FieldType.Decimal)]
    [InlineData(FieldType.Enum)]
    [InlineData(FieldType.Date)]
    [InlineData(FieldType.Reference)]
    public void Parse_Empty_IsNull(FieldType type) => P(F(type), "  ").Should().Be((null, null));
}
