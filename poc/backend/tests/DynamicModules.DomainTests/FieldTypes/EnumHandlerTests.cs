using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using static DynamicModules.DomainTests.FieldTypes.HandlerTestKit;

namespace DynamicModules.DomainTests.FieldTypes;

public class EnumHandlerTests
{
    [Fact]
    public void EnumHandler_ActiveOption_IsValid()
    {
        var r = Run(FieldType.Enum, "\"opt_5\"", ctx: Ctx(active: [5]));
        r.IsValid.Should().BeTrue();
        r.Value!.Value.GetString().Should().Be("opt_5");
    }

    [Theory]
    [InlineData("\"opt_9\"")]
    [InlineData("\"5\"")]
    [InlineData("\"opt_x\"")]
    [InlineData("5")]
    public void EnumHandler_UnknownOrMalformed_ReturnsError(string json) =>
        Run(FieldType.Enum, json, ctx: Ctx(active: [5])).Error.Should().Be("選項不存在或已停用");

    [Fact]
    public void EnumHandler_InactiveOption_AllowedOnlyWhenUnchanged()
    {
        // 選項 7 已停用（在 All 不在 Active）
        var unchanged = Ctx(active: [5], all: [5, 7], previous: "\"opt_7\"");
        Run(FieldType.Enum, "\"opt_7\"", ctx: unchanged).IsValid.Should().BeTrue();

        var created = Ctx(active: [5], all: [5, 7]);
        Run(FieldType.Enum, "\"opt_7\"", ctx: created).Error.Should().Be("選項不存在或已停用");

        var changedFromOther = Ctx(active: [5], all: [5, 7], previous: "\"opt_5\"");
        Run(FieldType.Enum, "\"opt_7\"", ctx: changedFromOther).Error.Should().Be("選項不存在或已停用");
    }

    [Fact]
    public void EnumHandler_Null_IsOkNull() => Run(FieldType.Enum, "null", ctx: Ctx(active: [5])).Value.Should().BeNull();

    [Fact]
    public void MultiEnumHandler_DedupesAndOrdersBySequence()
    {
        var ctx = Ctx(active: [1, 2, 3], seq: new() { [1] = 30, [2] = 10, [3] = 20 });
        var r = Run(FieldType.MultiEnum, "[\"opt_1\",\"opt_2\",\"opt_1\",\"opt_3\"]", ctx: ctx);
        r.IsValid.Should().BeTrue();
        r.Value!.Value.EnumerateArray().Select(e => e.GetString()).Should().Equal("opt_2", "opt_3", "opt_1");
    }

    [Fact]
    public void MultiEnumHandler_InactiveAllowedOnlyIfInPreviousArray()
    {
        var ctx = Ctx(active: [1], all: [1, 2, 3], seq: new() { [1] = 1, [2] = 2 }, previous: "[\"opt_2\"]");
        var ok = Run(FieldType.MultiEnum, "[\"opt_2\",\"opt_1\"]", ctx: ctx);
        ok.IsValid.Should().BeTrue();
        ok.Value!.Value.EnumerateArray().Select(e => e.GetString()).Should().Equal("opt_1", "opt_2");

        Run(FieldType.MultiEnum, "[\"opt_3\"]", ctx: ctx).Error.Should().Be("選項不存在或已停用");
    }

    [Theory]
    [InlineData("\"opt_1\"")]
    [InlineData("[1]")]
    [InlineData("[\"opt_99\"]")]
    public void MultiEnumHandler_Invalid_ReturnsError(string json) =>
        Run(FieldType.MultiEnum, json, ctx: Ctx(active: [1])).Error.Should().Be("選項不存在或已停用");

    [Fact]
    public void MultiEnumHandler_Null_IsOkNull() => Run(FieldType.MultiEnum, "null", ctx: Ctx(active: [1])).Value.Should().BeNull();

    [Fact]
    public void EnumHandler_NonCanonicalOptionValue_IsNormalized()
    {
        var r = Run(FieldType.Enum, "\"opt_05\"", ctx: Ctx(active: [5]));
        r.IsValid.Should().BeTrue();
        r.Value!.Value.GetString().Should().Be("opt_5");
    }

    [Fact]
    public void MultiEnumHandler_NormalizesAndDedupesById()
    {
        var r = Run(FieldType.MultiEnum, "[\"opt_5\",\"opt_05\"]", ctx: Ctx(active: [5]));
        r.IsValid.Should().BeTrue();
        r.Value!.Value.EnumerateArray().Select(e => e.GetString()).Should().Equal("opt_5");
    }

    [Fact]
    public void MultiEnumHandler_KeepsUnchangedLegacyValue_LikeEnum()
    {
        var r = Run(FieldType.MultiEnum, "[\"legacy\",\"opt_1\"]", ctx: Ctx(active: [1], previous: "[\"legacy\"]"));
        r.IsValid.Should().BeTrue();
        r.Value!.Value.EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo("legacy", "opt_1");
    }
}
