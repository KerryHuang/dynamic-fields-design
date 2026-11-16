using System.Text.Json;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.DomainTests.ValueObjects;

public class FieldConfigTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void FieldConfig_Rollup_RoundTripsJson()
    {
        const string json = "{\"rollup\":{\"sourceFieldId\":3,\"function\":\"sum\",\"aggregateFieldId\":5,\"condition\":{\"op\":\"eq\"}},\"relation\":\"detail\",\"scope\":\"parent\"}";
        var config = JsonSerializer.Deserialize<FieldConfig>(json, Web)!;
        config.Relation.Should().Be(DynamicModules.Domain.Entities.Dynamic.ReferenceRelation.Detail);
        config.Scope.Should().Be("parent");
        config.Rollup!.SourceFieldId.Should().Be(3);
        config.Rollup.Function.Should().Be(DynamicModules.Domain.Entities.Dynamic.RollupFunction.Sum);
        config.Rollup.AggregateFieldId.Should().Be(5);
        JsonSerializer.Serialize(config, Web).Should().Contain("\"rollup\":{\"sourceFieldId\":3,\"function\":\"sum\",\"aggregateFieldId\":5,\"condition\":{\"op\":\"eq\"}}")
            .And.Contain("\"relation\":\"detail\"").And.Contain("\"scope\":\"parent\"");
    }

    [Fact]
    public void FieldConfig_RoundTripsJson()
    {
        var config = new FieldConfig
        {
            Required = true,
            ReadOnly = true,
            MaxLength = 50,
            Precision = 10,
            Scale = 2,
            DefaultValue = JsonDocument.Parse("\"abc\"").RootElement.Clone(),
            ShowInList = true
        };

        var json = JsonSerializer.Serialize(config, Web);
        json.Should().Contain("\"maxLength\":50").And.Contain("\"showInList\":true").And.Contain("\"defaultValue\":\"abc\"");

        var back = JsonSerializer.Deserialize<FieldConfig>(json, Web)!;
        back.Should().BeEquivalentTo(config, o => o.Excluding(c => c.DefaultValue));
        back.DefaultValue!.Value.GetString().Should().Be("abc");
    }

    [Fact]
    public void FieldConfig_Defaults_AreFalseAndNull()
    {
        var c = JsonSerializer.Deserialize<FieldConfig>("{}", Web)!;
        (c.Required, c.ReadOnly, c.ShowInList).Should().Be((false, false, false));
        (c.MaxLength, c.Precision, c.Scale, c.DefaultValue).Should().Be((null, null, null, null));
    }

    [Fact]
    public void FieldConfig_RoundTripsFilterableAndSortable()
    {
        var json = JsonSerializer.Serialize(new FieldConfig { IsFilterable = true, IsSortable = true }, Web);
        json.Should().Contain("\"isFilterable\":true").And.Contain("\"isSortable\":true");

        var back = JsonSerializer.Deserialize<FieldConfig>(json, Web)!;
        (back.IsFilterable, back.IsSortable).Should().Be((true, true));

        var old = JsonSerializer.Deserialize<FieldConfig>("{\"required\":true}", Web)!;
        (old.IsFilterable, old.IsSortable).Should().Be((false, false));
    }
}
