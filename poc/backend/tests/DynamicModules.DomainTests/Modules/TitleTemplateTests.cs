using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Modules;

/// <summary>模組的標題樣板：{} 內是欄位（資料庫存欄位 Id，套件存欄位代號），其餘照原樣。</summary>
public class TitleTemplateTests
{
    private static I18nText Zh(string s) => new([KeyValuePair.Create("zh-TW", s), KeyValuePair.Create("en-US", s + "-en")]);

    private static Module Mold(string? template) => new()
    {
        Id = 1, Key = "mold", TitleFieldId = 11, TitleTemplate = template,
        Blocks =
        [
            new Block
            {
                Fields =
                [
                    new Field { Id = 10, Key = "f_10", Type = FieldType.AutoNumber, LabelI18n = Zh("編號") },
                    new Field { Id = 11, Key = "f_11", Type = FieldType.Text, LabelI18n = Zh("名稱") },
                    new Field { Id = 12, Key = "f_12", Type = FieldType.Date, LabelI18n = Zh("日期") },
                    new Field
                    {
                        Id = 13, Key = "f_13", Type = FieldType.Enum, LabelI18n = Zh("類別"),
                        Options = [new FieldOption { Id = 99, LabelI18n = Zh("塑膠模") }]
                    },
                ]
            }
        ]
    };

    private static Dictionary<string, JsonElement?> Values(params (string Key, object? Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(v.Value));

    [Fact]
    public void Parse_SplitsTokensAndText()
    {
        var (parts, error) = TitleTemplate.Parse("{no} - {name}");
        error.Should().BeNull();
        parts.Should().Equal(new TitleTemplate.Part(null, "no"), new TitleTemplate.Part(" - ", null), new TitleTemplate.Part(null, "name"));
        TitleTemplate.Tokens("{no} {name}").Should().Equal("no", "name");
    }

    [Theory]
    [InlineData("", "標題樣板是空的")]
    [InlineData("  ", "標題樣板是空的")]
    [InlineData("{no", "標題樣板的大括號沒有成對")]
    [InlineData("no}", "標題樣板的大括號沒有成對")]
    [InlineData("{a{b}}", "標題樣板的大括號沒有成對")]
    [InlineData("{} x", "標題樣板有空的 {}")]
    [InlineData("只有文字", "標題樣板至少要引用一個欄位")]
    public void Parse_Errors(string template, string message) =>
        TitleTemplate.Parse(template).Error.Should().Be(message);

    [Fact]
    public void Parse_TooLong() =>
        TitleTemplate.Parse("{a}" + new string('x', 200)).Error.Should().Be($"標題樣板不能超過 {TitleTemplate.MaxLength} 字");

    [Fact]
    public void Render_TextAutonumberDateAndOptionLabel()
    {
        var m = Mold("{10} {11}／{12}／{13}");
        TitleTemplate.Render(m, Values(("f_10", "M-0001"), ("f_11", "外殼上蓋模"), ("f_12", "2026-11-01"), ("f_13", "opt_99")))
            .Should().Be("M-0001 外殼上蓋模／2026-11-01／塑膠模");
    }

    [Fact]
    public void Render_MissingValuesAreEmpty_AndTheResultIsTrimmed()
    {
        var m = Mold("{10} {11}");
        TitleTemplate.Render(m, Values(("f_11", "外殼上蓋模"))).Should().Be("外殼上蓋模");
        TitleTemplate.Render(m, Values(("f_10", null), ("f_11", "外殼上蓋模"))).Should().Be("外殼上蓋模");
        TitleTemplate.Render(m, Values()).Should().Be("");
    }

    [Fact]
    public void Render_UnknownTokenOrOption_IsEmpty()
    {
        TitleTemplate.Render(Mold("{10}-{404}"), Values(("f_10", "M-1"))).Should().Be("M-1-");
        TitleTemplate.Render(Mold("{13}"), Values(("f_13", "opt_1"))).Should().Be("");
    }

    [Fact]
    public void Module_TitleFieldIds_IsTheTitleFieldPlusTemplateFields()
    {
        Mold(null).TitleFieldIds().Should().Equal(11);
        Mold("{10} {11}").TitleFieldIds().Should().BeEquivalentTo([10L, 11L]);
        Mold("{10} {12}").TitleFieldIds().Should().BeEquivalentTo([10L, 11L, 12L]);
    }

    [Fact]
    public void Map_RewritesTokens_AndKeepsText()
    {
        TitleTemplate.Map("{no} - {name}", code => code == "no" ? "10" : "11").Should().Be("{10} - {11}");
        TitleTemplate.Map("{no} {gone}", code => code == "no" ? "10" : null).Should().BeNull();
    }
}
