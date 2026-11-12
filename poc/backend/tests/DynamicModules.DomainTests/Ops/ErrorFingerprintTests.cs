using DynamicModules.Domain.Ops;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Ops;

public class ErrorFingerprintTests
{
    [Theory]
    [InlineData("Record 12 not found", "Record {n} not found")]
    [InlineData("Record 345 not found", "Record {n} not found")]
    [InlineData("id 7d9f0c1e-1b2a-4c3d-9e8f-0a1b2c3d4e5f missing", "id {guid} missing")]
    [InlineData("user 'Alice' and \"Bob\"", "user {s} and {s}")]
    [InlineData("mail amy@example.test failed", "mail {email} failed")]
    [InlineData("token deadbeef01 bad", "token {hex} bad")]
    [InlineData("count 12345678 ok", "count {n} ok")]   // 純數字不算十六進位
    public void NormalizeMessage_ReplacesVolatileParts(string raw, string expected) =>
        ErrorFingerprint.NormalizeMessage(raw).Should().Be(expected);

    [Fact] public void NormalizeMessage_TruncatesTo200() =>
        ErrorFingerprint.NormalizeMessage(new string('z', 300)).Length.Should().Be(200);

    [Theory]
    [InlineData("DynamicModules.App.Foo+<Handle>d__5", "MoveNext")]
    [InlineData("DynamicModules.App.Foo", "Handle")]
    [InlineData("DynamicModules.App.Foo+<>c__DisplayClass3_0", "<Handle>b__0")]
    [InlineData("DynamicModules.App.Foo", "<Handle>g__Local|3_0")]
    public void NormalizeFrame_AsyncLambdaLocal_RestoreToMethod(string type, string method) =>
        ErrorFingerprint.NormalizeFrame(type, method).Should().Be("DynamicModules.App.Foo.Handle");

    [Fact]
    public void ForServer_SameDefectDifferentNumbersAndNonProductFrames_SameFingerprint()
    {
        var a = ErrorFingerprint.ForServer("System.InvalidOperationException", "Record 12 not found",
            ["Microsoft.X.Y.Z", "DynamicModules.App.Foo.Handle", "DynamicModules.App.Bar.Run"], "api/v3/dynamic/modules/{key}");
        var b = ErrorFingerprint.ForServer("System.InvalidOperationException", "Record 99 not found",
            ["DynamicModules.App.Foo.Handle", "DynamicModules.App.Bar.Run"], "api/v3/dynamic/modules/{key}");
        a.Should().Be(b).And.MatchRegex("^[0-9a-f]{16}$");
    }

    [Fact]
    public void ForServer_DifferentExceptionType_DifferentFingerprint() =>
        ErrorFingerprint.ForServer("System.InvalidOperationException", "x", [], null)
            .Should().NotBe(ErrorFingerprint.ForServer("System.ArgumentException", "x", [], null));

    [Fact]
    public void ForServer_AsyncFrameEqualsSyncFrame()
    {
        var async = ErrorFingerprint.NormalizeFrame("DynamicModules.App.Foo+<Handle>d__5", "MoveNext");
        ErrorFingerprint.ForServer("E", "m", [async], null)
            .Should().Be(ErrorFingerprint.ForServer("E", "m", ["DynamicModules.App.Foo.Handle"], null));
    }

    [Theory]
    [InlineData("at f (https://h/assets/DynamicForm-RZEY-5W4.js:1:2)", "f@DynamicForm.js")]   // 雜湊含 -
    [InlineData("at g (https://h/assets/api-error-sXABj2Xu.js:1:2)", "g@api-error.js")]
    [InlineData("at h (https://h/assets/my-long-name.js:1:2)", "h@my-long-name.js")]   // 後綴不是 8 字元，不能吃掉
    public void ClientFrames_ViteHash_WithDashOrPlainName(string line, string expected) =>
        ErrorFingerprint.ClientFrames(line).Should().Equal(expected);

    [Fact]
    public void ClientFrames_ChromeAndFirefox_StripHashAndLineNumbers()
    {
        ErrorFingerprint.ClientFrames("TypeError: x\n    at render (https://h/assets/IndexPage-BxY12abc.js:1:200)\n    at a (https://h/assets/vendor-Q9w8E7r6.js:3:4)")
            .Should().Equal("render@IndexPage.js", "a@vendor.js");
        ErrorFingerprint.ClientFrames("render@https://h/assets/IndexPage-BxY12abc.js:1:200")
            .Should().Equal("render@IndexPage.js");
    }

    [Fact]
    public void ForClient_LineNumbersAndVersionDoNotMatter() =>
        ErrorFingerprint.ForClient("vue", "TypeError", "x is 3", ErrorFingerprint.ClientFrames("at f (https://h/a-AAAAAAAA.js:1:2)"), "/dynamic/:moduleKey")
            .Should().Be(ErrorFingerprint.ForClient("vue", "TypeError", "x is 4", ErrorFingerprint.ClientFrames("at f (https://h/a-BBBBBBBB.js:9:9)"), "/dynamic/:moduleKey"));
}
