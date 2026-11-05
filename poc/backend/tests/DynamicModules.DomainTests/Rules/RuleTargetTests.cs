using DynamicModules.Domain.Rules;
using FluentAssertions;

namespace DynamicModules.DomainTests.Rules;

public class RuleTargetTests
{
    [Fact]
    public void TryParse_ValidForms()
    {
        RuleTarget.TryParse("field:12", out var f).Should().BeTrue();
        f.Should().Be(new FieldTarget(12));
        RuleTarget.TryParse("block:3", out var b).Should().BeTrue();
        b.Should().Be(new BlockTarget(3));
        RuleTarget.TryParse("*", out var r).Should().BeTrue();
        r.Should().BeOfType<RecordTarget>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("field:")]
    [InlineData("field:0")]
    [InlineData("field:012")]
    [InlineData("field:-1")]
    [InlineData("field: 1")]
    [InlineData("field:1.5")]
    [InlineData("Field:1")]
    [InlineData("record:1")]
    [InlineData("**")]
    public void TryParse_Invalid(string s) => RuleTarget.TryParse(s, out _).Should().BeFalse();
}
