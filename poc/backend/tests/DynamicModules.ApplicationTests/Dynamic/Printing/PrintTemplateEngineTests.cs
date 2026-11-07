using DynamicModules.Application.Dynamic.PrintService;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.Printing;

public class PrintTemplateEngineTests
{
    private static Func<string, string?> Values(params (string Key, string Value)[] pairs) =>
        key => pairs.Where(p => p.Key == key).Select(p => p.Value).FirstOrDefault();

    [Fact]
    public void ReplacesHeaderPlaceholders() =>
        PrintTemplateEngine.Render("<p>{{f_1}} / {{ f_2 }}</p>", Values(("f_1", "甲"), ("f_2", "乙")), _ => []).Should().Be("<p>甲 / 乙</p>");

    [Fact]
    public void ValuesAreHtmlEscaped()
    {
        var html = PrintTemplateEngine.Render("<td>{{f_1}}</td>", Values(("f_1", "<script>alert(1)</script>\"'&")), _ => []);
        html.Should().Be("<td>&lt;script&gt;alert(1)&lt;/script&gt;&quot;&#39;&amp;</td>");
    }

    [Fact]
    public void ValueInsideAttributeCannotBreakOut()
    {
        var html = PrintTemplateEngine.Render("""<td class="{{f_1}}">x</td>""", Values(("f_1", "\" onclick=\"alert(1)")), _ => []);
        html.Should().Be("""<td class="&quot; onclick=&quot;alert(1)">x</td>""");
    }

    [Fact]
    public void NewlinesInValuesBecomeLineBreaks() =>
        PrintTemplateEngine.Render("{{f_1}}", Values(("f_1", "a\r\nb\nc")), _ => []).Should().Be("a<br>b<br>c");

    [Fact]
    public void UnknownOrMissingPlaceholderIsEmptyNotAnError()
    {
        PrintTemplateEngine.Render("[{{nope}}][{{f_9}}]", Values(), _ => []).Should().Be("[][]");
    }

    [Fact]
    public void LoopRepeatsOncePerRowWithRowValuesAndIndex()
    {
        IReadOnlyList<Func<string, string?>> rows = [Values(("f_5", "A")), Values(("f_5", "B"))];
        var html = PrintTemplateEngine.Render("{{#each m-line}}<tr><td>{{_index}}</td><td>{{f_5}}</td></tr>{{/each}}", Values(), m => m == "m-line" ? rows : []);
        html.Should().Be("<tr><td>1</td><td>A</td></tr><tr><td>2</td><td>B</td></tr>");
    }

    [Fact]
    public void LoopOverUnknownOrEmptyModuleRendersNothing() =>
        PrintTemplateEngine.Render("a{{#each nope}}X{{/each}}b", Values(), _ => []).Should().Be("ab");

    [Fact]
    public void RowPlaceholderDoesNotFallBackToHeaderValues()
    {
        IReadOnlyList<Func<string, string?>> rows = [Values()];
        PrintTemplateEngine.Render("{{#each m-line}}[{{f_1}}]{{/each}}", Values(("f_1", "header")), _ => rows).Should().Be("[]");
    }

    [Fact]
    public void OutputOfOnePlaceholderIsNeverReinterpretedAsAnother() =>
        PrintTemplateEngine.Render("{{f_1}}|{{f_2}}", Values(("f_1", "{{f_2}}"), ("f_2", "secret")), _ => []).Should().Be("{{f_2}}|secret");

    [Fact]
    public void Placeholders_ListsHeaderKeysAndLoopKeys()
    {
        var p = PrintTemplateEngine.Placeholders("{{f_1}}{{#each m-line}}{{f_5}}{{_index}}{{/each}}{{_title}}");
        p.Header.Should().BeEquivalentTo(["f_1"]);
        p.Loops.Should().ContainKey("m-line").WhoseValue.Should().BeEquivalentTo(["f_5"]);
        p.Error.Should().BeNull();
    }

    [Theory]
    [InlineData("{{#each m-line}}no end")]
    [InlineData("stray {{/each}}")]
    [InlineData("{{#each a}}{{#each b}}x{{/each}}{{/each}}")]
    public void Placeholders_ReportsBrokenLoops(string body) => PrintTemplateEngine.Placeholders(body).Error.Should().NotBeNull();
}
