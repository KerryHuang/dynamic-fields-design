using System.Text.Json;
using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>套件編碼器匯出方向的回歸（第一輪實作審查 c）：固定記錄 Id 不能原樣寫進套件。</summary>
public class AppEffectsCodecTests
{
    private static AppReportCodec.Names NamesWith(FieldType? identity) =>
        new(_ => "code", id => $"m.f_{id}", (_, _) => null, _ => false, _ => identity);

    private static ModuleEvent EventSetting(string constJson) => new()
    {
        Key = "t-event", NameI18n = new I18nText([KeyValuePair.Create("zh-TW", "t")]),
        Definition = new EventDefinition(new EventTrigger("create", null, null, null), null,
        [
            new EventEffect("e", "create", "target", null, null,
                [new EventSet(7, null, null, JsonDocument.Parse(constJson).RootElement.Clone(), false)], null, null, null, null)
        ])
    };

    [Theory]
    [InlineData(FieldType.Reference)]
    [InlineData(FieldType.User)]
    public void A_constant_on_a_reference_or_user_field_is_not_exported(FieldType identity)
    {
        var (package, errors) = AppEffectsCodec.ToPackage(EventSetting("42"), new Module { Key = "src" }, _ => "code", NamesWith(identity));

        package.Should().BeNull();
        errors.Should().Contain(e => e.Contains("套件不收"));
    }

    [Fact]
    public void A_constant_on_an_ordinary_field_is_exported()
    {
        var (package, errors) = AppEffectsCodec.ToPackage(EventSetting("\"x\""), new Module { Key = "src" }, _ => "code", NamesWith(null));

        errors.Should().BeEmpty();
        package.Should().NotBeNull();
    }
}
