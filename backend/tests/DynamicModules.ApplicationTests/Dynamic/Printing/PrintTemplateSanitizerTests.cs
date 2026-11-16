using DynamicModules.Application.Dynamic.PrintService;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.Printing;

public class PrintTemplateSanitizerTests
{
    private static string S(string html) => PrintTemplateSanitizer.Sanitize(html);

    [Fact]
    public void KeepsPlainLayoutMarkupAndPlaceholders()
    {
        const string html = """<div class="head"><h1>報價單 {{f_1}}</h1><table><thead><tr><th colspan="2" align="right">品名</th></tr></thead><tbody>{{#each m-line}}<tr><td>{{f_2}}</td></tr>{{/each}}</tbody></table></div>""";
        S(html).Should().Be(html);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<ScRiPt>alert(1)</sCrIpT>")]
    [InlineData("<script src=x></script>")]
    [InlineData("<script\n>alert(1)</script\n>")]
    [InlineData("<script>alert(1)")]
    public void RemovesScriptElementsIncludingTheirContent(string html)
    {
        var result = S("a" + html + "b");
        result.ToLowerInvariant().Should().NotContain("<script").And.NotContain("alert(1)");
        result.Should().StartWith("a");
    }

    [Theory]
    [InlineData("""<div onclick="alert(1)">x</div>""")]
    [InlineData("""<div ONMOUSEOVER=alert(1)>x</div>""")]
    [InlineData("""<div onload = 'alert(1)'>x</div>""")]
    [InlineData("<div/onclick=alert(1)>x</div>")]
    [InlineData("""<table onfocus="a()"><tr><td onerror="a()">x</td></tr></table>""")]
    public void RemovesEventHandlerAttributes(string html) =>
        S(html).ToLowerInvariant().Should().NotContain("onclick").And.NotContain("onmouseover").And.NotContain("onload")
            .And.NotContain("onfocus").And.NotContain("onerror").And.NotContain("alert");

    [Theory]
    [InlineData("""<a href="javascript:alert(1)">x</a>""")]
    [InlineData("""<a href="JaVaScRiPt:alert(1)">x</a>""")]
    [InlineData("""<a href="  javascript:alert(1)">x</a>""")]
    [InlineData("<a href=\"java\tscript:alert(1)\">x</a>")]
    [InlineData("<a href=\"java\nscript:alert(1)\">x</a>")]
    [InlineData("""<a href="jav&#x61;script:alert(1)">x</a>""")]
    [InlineData("""<a href="&#106;avascript:alert(1)">x</a>""")]
    [InlineData("""<a href="data:text/html,<script>alert(1)</script>">x</a>""")]
    [InlineData("""<a href="vbscript:msgbox(1)">x</a>""")]
    [InlineData("""<a href=javascript:alert(1)>x</a>""")]
    public void RemovesDangerousLinkTargets(string html)
    {
        var result = S(html).ToLowerInvariant();
        result.Should().NotContain("href").And.NotContain("javascript").And.NotContain("vbscript").And.NotContain("data:");
        result.Should().Contain(">x<");
    }

    [Theory]
    [InlineData("""<a href="https://example.com/a?b=1&c=2">x</a>""", "https://example.com/a?b=1&amp;c=2")]
    [InlineData("""<a href="mailto:a@example.com">x</a>""", "mailto:a@example.com")]
    public void KeepsSafeLinks(string html, string href) => S(html).Should().Contain($"href=\"{href}\"");

    [Theory]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>")]
    [InlineData("<object data=x></object>")]
    [InlineData("<embed src=x>")]
    [InlineData("<svg onload=alert(1)><circle/></svg>")]
    [InlineData("<math><mi xlink:href=\"javascript:alert(1)\">x</mi></math>")]
    [InlineData("<form action=\"https://evil.example\"><input name=a></form>")]
    [InlineData("<base href=\"https://evil.example/\">")]
    [InlineData("<link rel=stylesheet href=\"https://evil.example/a.css\">")]
    [InlineData("<meta http-equiv=refresh content=\"0;url=https://evil.example\">")]
    [InlineData("<style>body{background:url(https://evil.example)}</style>")]
    public void RemovesEmbeddingAndActiveElements(string html)
    {
        var result = S("<p>keep</p>" + html).ToLowerInvariant();
        foreach (var bad in new[] { "<img", "<iframe", "<object", "<embed", "<svg", "<math", "<form", "<input", "<base", "<link", "<meta", "<style", "evil.example", "alert", "onerror", "onload" })
            result.Should().NotContain(bad);
        result.Should().Contain("<p>keep</p>");
    }

    [Theory]
    [InlineData("""<div style="background:url(javascript:alert(1))">x</div>""")]
    [InlineData("""<div style="width:expression(alert(1))">x</div>""")]
    [InlineData("""<div style="background-image:url('https://evil.example/a.png')">x</div>""")]
    [InlineData("""<div style="position:fixed;top:0;left:0;width:100%;height:100%">x</div>""")]
    [InlineData("""<div style="behavior:url(x.htc)">x</div>""")]
    public void StyleAttributeKeepsOnlySafeDeclarations(string html)
    {
        var result = S(html).ToLowerInvariant();
        result.Should().NotContain("url").And.NotContain("expression").And.NotContain("javascript").And.NotContain("position").And.NotContain("behavior");
    }

    [Fact]
    public void StyleAttributeKeepsPlainLayoutDeclarations() =>
        S("""<td style="text-align:right; font-weight:bold; width:30%">x</td>""").Should().Contain("text-align:right").And.Contain("font-weight:bold").And.Contain("width:30%");

    [Fact]
    public void BrokenUpScriptTagIsOnlyInertText() =>
        S("<scr<script>ipt>alert(1)</scr</script>ipt>").Should().NotContain("<");

    [Fact]
    public void RemovesCommentsAndConditionalComments() =>
        S("a<!-- <script>alert(1)</script> -->b<!--[if IE]><script>x</script><![endif]-->c<![CDATA[<script>]]>d")
            .Should().NotContain("<").And.NotContain("script");

    [Fact]
    public void EscapesStrayAngleBracketsAndAmpersands()
    {
        S("1 < 2 && 3 > 2").Should().Be("1 &lt; 2 &amp;&amp; 3 &gt; 2");
        S("a &amp; b &#169; c").Should().Be("a &amp; b &#169; c");
    }

    [Fact]
    public void UnterminatedTagBecomesInertText()
    {
        var result = S("ok<div onclick=alert(1)");
        result.Should().NotContain("<div");
        result.Should().Contain("ok");
    }

    [Fact]
    public void UnknownAndCustomTagsAreDroppedButTheirTextIsKept() =>
        S("<blink>hi</blink><x-evil onclick=a()>there</x-evil>").Should().Be("hithere");

    [Fact]
    public void ClosesTagsThatWereLeftOpenAndIgnoresStrayCloseTags() =>
        S("<div><p>a</span></p>b").Should().Be("<div><p>a</p>b</div>");

    [Fact]
    public void NullBytesAndControlCharsInNamesAreHarmless()
    {
        var result = S("<scr\0ipt>alert(1)</scr\0ipt><div on\0click=a()>x</div>").ToLowerInvariant();
        result.Should().NotContain("<script").And.NotContain("onclick").And.NotContain("<scr");
    }

    [Fact]
    public void IsIdempotent()
    {
        var once = S("""<div onclick=a() class="x"><a href="https://e.example">l</a><script>1</script>{{f_1}}<br></div>""");
        S(once).Should().Be(once);
    }

    [Fact]
    public void AttributeValuesAreEscaped() =>
        S("""<a title='a"b<c'>x</a>""").Should().Be("<a title=\"a&quot;b&lt;c\">x</a>");
}
