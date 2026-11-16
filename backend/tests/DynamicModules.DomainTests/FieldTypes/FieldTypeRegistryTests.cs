using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.FieldTypes.Handlers;
using FluentAssertions;

namespace DynamicModules.DomainTests.FieldTypes;

public class FieldTypeRegistryTests
{
    [Fact]
    public void Registry_AllTypesRegistered()
    {
        var registry = FieldTypeRegistry.CreateDefault();
        var all = Enum.GetValues<FieldType>();
        all.Should().HaveCount(21);
        foreach (var type in all)
            registry.Get(type).Type.Should().Be(type);
    }

    [Fact]
    public void Registry_HasRollupHandler() =>
        FieldTypeRegistry.CreateDefault().Get(FieldType.Rollup).Should().BeOfType<RollupHandler>();

    [Fact]
    public void Registry_MissingType_Throws()
    {
        var registry = new FieldTypeRegistry([new TextHandler()]);
        var act = () => registry.Get(FieldType.User);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Registry_DuplicateType_Throws()
    {
        var act = () => new FieldTypeRegistry([new TextHandler(), new TextHandler()]);
        act.Should().Throw<InvalidOperationException>();
    }
}
