using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.FieldTypes.Handlers;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Xunit;
using static DynamicModules.DomainTests.FieldTypes.HandlerTestKit;

namespace DynamicModules.DomainTests.FieldTypes;

public class ReferenceHandlerTests
{
    private const string NotFound = "參照的資料不存在或無權選取";

    private static FieldConfig Module => new() { RefTarget = new ReferenceTarget(ReferenceTarget.ModuleKind, "customer") };
    private static FieldConfig Entity => new() { RefTarget = new ReferenceTarget(ReferenceTarget.EntityKind, "design.mold") };

    [Fact]
    public void Normalize_ModuleTarget_NumberOk() =>
        Run(FieldType.Reference, "42", Module).IsValid.Should().BeTrue();

    [Fact]
    public void Normalize_ModuleTarget_StringRejected() =>
        Run(FieldType.Reference, "\"42\"", Module).Error.Should().Be(NotFound);

    [Fact]
    public void Normalize_EntityTarget_NumberRejected() =>
        Run(FieldType.Reference, "42", Entity).Error.Should().Be(NotFound);

    [Fact]
    public void Normalize_EntityTarget_StringOk() =>
        Run(FieldType.Reference, "\"M-0001\"", Entity).IsValid.Should().BeTrue();

    [Fact]
    public void Normalize_Null_OkNull()
    {
        var r = Run(FieldType.Reference, "null", Module);
        r.IsValid.Should().BeTrue();
        r.Value.Should().BeNull();
    }

    [Fact]
    public void Normalize_NotInValidKeys_Rejected() =>
        Run(FieldType.Reference, "42", Module, Ctx() with { ValidRefKeys = new HashSet<string> { "1" } })
            .Error.Should().Be(NotFound);

    [Fact]
    public void Normalize_InValidKeys_Ok() =>
        Run(FieldType.Reference, "42", Module, Ctx() with { ValidRefKeys = new HashSet<string> { "42" } })
            .IsValid.Should().BeTrue();

    [Fact]
    public void Normalize_ValidKeysNull_Skipped() =>
        Run(FieldType.Reference, "42", Module, Ctx()).IsValid.Should().BeTrue();

    [Fact]
    public void Normalize_UnchangedFromPrevious_NotChecked() =>
        Run(FieldType.Reference, "42", Module, Ctx(previous: "42") with { ValidRefKeys = new HashSet<string>() })
            .IsValid.Should().BeTrue();

    [Theory]
    [InlineData("42", "42")]
    [InlineData("\"M-0001\"", "M-0001")]
    public void ToIndex_PutsStringInRefKey(string json, string expected)
    {
        var index = FieldTypeRegistry.CreateDefault().Get(FieldType.Reference).ToIndex(J(json), F(FieldType.Reference));
        index.Should().ContainSingle().Which.Should().Be(new IndexValue(0, null, null, null, expected));
    }

    [Fact]
    public void KeyOf_NumberStringOther()
    {
        ReferenceHandler.KeyOf(J("42")).Should().Be("42");
        ReferenceHandler.KeyOf(J("\"M-1\"")).Should().Be("M-1");
        ReferenceHandler.KeyOf(J("true")).Should().BeNull();
    }
}
