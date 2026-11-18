using DynamicModules.Domain.Tasks;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Tasks;

/// <summary>甘特設定格式（看板設計 §11.5.2.7，GT-T8）：寫入嚴格、讀取寬鬆、禁止 dueRange。</summary>
public class TaskGanttConfigTests
{
    private const string Valid = """{"version":1,"groupBy":"parent","scope":"assigned","scale":"week"}""";

    [Fact]
    public void Default_IsParentAssignedWeek_NoCancelled_AndParses()
    {
        var d = TaskGanttConfig.Default;

        (d.GroupBy, d.Scope, d.Scale, d.ShowSubtasks).Should().Be(("parent", "assigned", "week", (bool?)null));
        d.Filters!.Statuses.Should().Equal("todo", "inProgress", "done");
        TaskGanttConfig.Parse(d.Serialize()).Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"version":1,"groupBy":"status","scope":"assigned","scale":"week"}""", TaskGanttConfigErrors.GroupBy)]
    [InlineData("""{"version":1,"groupBy":"parent","scope":"mine","scale":"week"}""", TaskGanttConfigErrors.Scope)]
    [InlineData("""{"version":1,"groupBy":"parent","scope":"assigned","scale":"year"}""", TaskGanttConfigErrors.Scale)]
    [InlineData("""{"version":1,"groupBy":"parent","scope":"assigned"}""", TaskGanttConfigErrors.Scale)]
    [InlineData("""{"version":2,"groupBy":"parent","scope":"assigned","scale":"week"}""", TaskGanttConfigErrors.Version)]
    [InlineData("""{"version":1,"groupBy":"parent","scope":"assigned","scale":"week","colour":1}""", TaskGanttConfigErrors.Format)]
    [InlineData("""{"version":1,"groupBy":"parent","scope":"assigned","scale":"week","sort":{"key":"title"}}""", TaskGanttConfigErrors.Format)]
    [InlineData("""{"version":1,"groupBy":"parent","scope":"assigned","scale":"week","filters":{"dueRange":{"kind":"thisWeek"}}}""", TaskGanttConfigErrors.NoDueRange)]
    [InlineData("""{"version":1,"groupBy":"parent","scope":"assigned","scale":"week","filters":{"statuses":["archived"]}}""", TaskGanttConfigErrors.Statuses)]
    [InlineData("""{"version":1,"groupBy":"parent","scope":"assigned","scale":"week","filters":{"sources":["mail"]}}""", TaskGanttConfigErrors.Sources)]
    [InlineData("""{"version":1,"groupBy":"parent","scope":"assigned","scale":"week","filters":{"assigneeUserIds":[" "]}}""", TaskGanttConfigErrors.PeopleBlank)]
    [InlineData("null", TaskGanttConfigErrors.Required)]
    public void Parse_Invalid_ReturnsKey(string json, string key)
    {
        var (config, errors) = TaskGanttConfig.Parse(json);

        config.Should().BeNull();
        errors.Should().ContainSingle().Which.Key.Should().Be(key);
    }

    [Fact]
    public void Parse_Valid_KeepsFilters_OmitsUnsetOnSerialize()
    {
        var (config, errors) = TaskGanttConfig.Parse(
            """{"version":1,"groupBy":"assignee","scope":"visible","scale":"month","showSubtasks":false,"filters":{"keyword":"expo","sources":["rule"]}}""");

        errors.Should().BeEmpty();
        config!.Serialize().Should().Be(
            """{"version":1,"groupBy":"assignee","scope":"visible","filters":{"sources":["rule"],"keyword":"expo"},"scale":"month","showSubtasks":false}""");
        TaskGanttConfig.Parse(Valid).Config!.Serialize().Should().Be(Valid);
    }

    [Fact]
    public void Parse_KeywordTooLong_CarriesMax()
    {
        var (_, errors) = TaskGanttConfig.Parse(
            "{\"version\":1,\"groupBy\":\"parent\",\"scope\":\"assigned\",\"scale\":\"week\",\"filters\":{\"keyword\":\"" + new string('a', 101) + "\"}}");

        errors.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Key = TaskGanttConfigErrors.KeywordTooLong, Args = new object[] { 100 } });
    }

    [Fact]
    public void Read_Lenient_UnknownIgnored_BadValuesDefault_DueRangeDropped()
    {
        var c = TaskGanttConfig.Read(
            """{"version":3,"groupBy":"status","scope":"x","scale":"year","future":1,"filters":{"statuses":["todo","archived"],"dueRange":{"kind":"thisWeek"}}}""");

        (c.Version, c.GroupBy, c.Scope, c.Scale).Should().Be((1, "parent", "assigned", "week"));
        c.Filters!.Statuses.Should().Equal("todo");
        c.Filters.DueRange.Should().BeNull();
        TaskGanttConfig.Read("not json").Should().Be(TaskGanttConfig.Default);
        TaskGanttConfig.Read("null").Should().Be(TaskGanttConfig.Default);
    }
}
