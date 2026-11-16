using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.FieldTypes.Handlers;
using FluentAssertions;

namespace DynamicModules.DomainTests.FieldTypes;

public class AttachmentHandlerTests
{
    private static readonly string IdA = new('a', 32);
    private static readonly string IdB = new('b', 32);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static FieldTypeContext Ctx(string? previous) =>
        new(new HashSet<long>(), new HashSet<long>(), new Dictionary<long, int>(),
            previous is null ? null : Json(previous), new HashSet<string>());

    [Fact]
    public void Normalize_Null_IsEmpty() =>
        new AttachmentHandler().Normalize(null, new Field(), Ctx(null)).Value.Should().BeNull();

    [Fact]
    public void Normalize_KeepsExistingFiles_WithServerSideNameAndSize()
    {
        var previous = $$"""[{"id":"{{IdA}}","name":"real.pdf","size":10}]""";
        var input = Json($$"""[{"id":"{{IdA}}","name":"hacked.exe","size":999}]""");
        var result = new AttachmentHandler().Normalize(input, new Field(), Ctx(previous));
        AttachmentValue.Parse(result.Value).Should().ContainSingle().Which.Should().Be(new AttachmentRef(IdA, "real.pdf", 10));
    }

    [Fact]
    public void Normalize_UnknownId_IsRejected()
    {
        var input = Json($$"""[{"id":"{{IdB}}","name":"x.txt","size":1}]""");
        var result = new AttachmentHandler().Normalize(input, new Field(), Ctx($$"""[{"id":"{{IdA}}","name":"a.txt","size":1}]"""));
        result.Error.Should().Be(AttachmentHandler.ForgedMessage);
    }

    [Fact]
    public void Normalize_NotAnArray_IsRejected() =>
        new AttachmentHandler().Normalize(Json("\"x\""), new Field(), Ctx(null)).Error.Should().NotBeNull();

    [Fact]
    public void Normalize_EmptyList_RemovesAll()
    {
        var result = new AttachmentHandler().Normalize(Json("[]"), new Field(), Ctx($$"""[{"id":"{{IdA}}","name":"a.txt","size":1}]"""));
        result.Error.Should().BeNull();
        result.Value.Should().BeNull();
    }
}
