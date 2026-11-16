using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using static DynamicModules.DomainTests.FieldTypes.HandlerTestKit;

namespace DynamicModules.DomainTests.FieldTypes;

public class SimpleHandlerTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void BooleanHandler_AcceptsBool(string json, bool expected)
    {
        var r = Run(FieldType.Boolean, json);
        r.IsValid.Should().BeTrue();
        r.Value!.Value.GetBoolean().Should().Be(expected);
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    public void BooleanHandler_RejectsNonBool(string json) =>
        Run(FieldType.Boolean, json).Error.Should().Be(FieldErrors.MustBeBoolean);

    [Fact]
    public void BooleanHandler_Null_IsOkNull() => Run(FieldType.Boolean, "null").Value.Should().BeNull();

    [Fact]
    public void EmailHandler_ValidAndInvalid()
    {
        Run(FieldType.Email, "\"a@b.com\"").IsValid.Should().BeTrue();
        foreach (var bad in new[] { "\"a@b\"", "\"a b@c.com\"", "\"@b.com\"", "1", "\"a@b.com\\n\"" })
            Run(FieldType.Email, bad).Error.Should().Be(FieldErrors.EmailFormat);
        Run(FieldType.Email, "null").IsValid.Should().BeTrue();
    }

    [Fact]
    public void PhoneHandler_ValidAndInvalid()
    {
        Run(FieldType.Phone, "\"+886 (2) 1234-5678 #12\"").IsValid.Should().BeTrue();
        foreach (var bad in new[] { "\"abc\"", "\"\"", $"\"{new string('1', 31)}\"", "123", "\"0912345678\\n\"", "\"02\\t1234\"" })
            Run(FieldType.Phone, bad).Error.Should().Be(FieldErrors.PhoneFormat);
        Run(FieldType.Phone, "null").IsValid.Should().BeTrue();
    }

    [Fact]
    public void UrlHandler_ValidAndInvalid()
    {
        Run(FieldType.Url, "\"https://example.com/a?b=1\"").IsValid.Should().BeTrue();
        Run(FieldType.Url, "\"http://example.com\"").IsValid.Should().BeTrue();
        foreach (var bad in new[] { "\"ftp://example.com\"", "\"example.com\"", "\"http://\"", "1" })
            Run(FieldType.Url, bad).Error.Should().Be(FieldErrors.UrlFormat);
        Run(FieldType.Url, "null").IsValid.Should().BeTrue();
    }

    [Fact]
    public void UserHandler_ValidAndInvalid()
    {
        var ctx = Ctx(users: ["alice", "bob"]);
        Run(FieldType.User, "\"alice\"", ctx: ctx).IsValid.Should().BeTrue();
        Run(FieldType.User, "\"eve\"", ctx: ctx).Error.Should().Be(FieldErrors.UserNotFound);
        Run(FieldType.User, "null", ctx: ctx).Value.Should().BeNull();
    }
}
