using DynamicModules.Domain.Localization;
using FluentAssertions;

namespace DynamicModules.DomainTests.Localization;

/// <summary>只有後端跑的字面量跳脫案例（審查必修 2）：前端 compiler 遇到超出範圍的碼位直接丟例外，不放共用案例檔。</summary>
public class UiMessageSyntaxTests
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    [Theory]
    [InlineData("{'\\U110000'}", LocalePackReasons.Syntax)]   // 超過 U+10FFFF：前端 String.fromCodePoint 會丟 RangeError
    [InlineData("{'\\U01F600'}", null)]                       // 補充平面的一般字
    [InlineData("{'\\uD800'}", null)]                         // 單獨代理：前端換成 U+FFFD，不是控制字元
    [InlineData("{'\\u000A'}", null)]                         // 跳脫成換行：和本文換行一樣合法
    [InlineData("{'\\U00009F'}", LocalePackReasons.Control)]  // C1 控制字元
    [InlineData("{'\\u2069'}", LocalePackReasons.Control)]
    [InlineData("{'a\\'}", LocalePackReasons.Syntax)]         // 反斜線吃掉收尾的單引號，字面量沒收尾
    [InlineData("{'a\\", LocalePackReasons.Syntax)]
    [InlineData("{'a\r\nb'}", LocalePackReasons.Syntax)]
    public void LiteralEscapes(string text, string? expected) => UiMessageSyntax.Check(text, None).Should().Be(expected);
}
