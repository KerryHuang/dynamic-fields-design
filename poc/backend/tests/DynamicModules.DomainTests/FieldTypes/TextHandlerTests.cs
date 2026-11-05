using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using static DynamicModules.DomainTests.FieldTypes.HandlerTestKit;

namespace DynamicModules.DomainTests.FieldTypes;

public class TextHandlerTests
{
    [Fact]
    public void TextHandler_TrimsValidValue()
    {
        var r = Run(FieldType.Text, "\"  abc \"");
        r.IsValid.Should().BeTrue();
        r.Value!.Value.GetString().Should().Be("abc");
    }

    [Fact]
    public void TextHandler_WhitespaceOnly_BecomesNull()
    {
        var r = Run(FieldType.Text, "\"   \"");
        (r.IsValid, r.Value).Should().Be((true, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    public void TextHandler_Null_IsOkNull(string? json)
    {
        var r = Run(FieldType.Text, json);
        (r.IsValid, r.Value).Should().Be((true, null));
    }

    [Fact]
    public void TextHandler_ExceedsDefault255_ReturnsError()
    {
        Run(FieldType.Text, $"\"{new string('a', 255)}\"").IsValid.Should().BeTrue();
        var r = Run(FieldType.Text, $"\"{new string('a', 256)}\"");
        (r.IsValid, r.Error).Should().Be((false, "超過 255 個字元"));
    }

    [Fact]
    public void TextHandler_UsesConfigMaxLength_AndTrimsBeforeCheck()
    {
        var cfg = new FieldConfig { MaxLength = 3 };
        Run(FieldType.Text, "\" abc \"", cfg).IsValid.Should().BeTrue();
        Run(FieldType.Text, "\"abcd\"", cfg).Error.Should().Be("超過 3 個字元");
    }

    [Fact]
    public void TextHandler_NonString_ReturnsError()
    {
        Run(FieldType.Text, "123").IsValid.Should().BeFalse();
    }

    [Fact]
    public void TextareaHandler_DefaultLimit4000()
    {
        Run(FieldType.Textarea, $"\"{new string('a', 4000)}\"").IsValid.Should().BeTrue();
        Run(FieldType.Textarea, $"\"{new string('a', 4001)}\"").Error.Should().Be("超過 4000 個字元");
        Run(FieldType.Textarea, "\" \"").Value.Should().BeNull();
    }
}
