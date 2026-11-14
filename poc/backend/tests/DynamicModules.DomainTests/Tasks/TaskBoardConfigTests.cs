using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Tasks;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Tasks;

/// <summary>看板設定格式（計畫 J6）：寫入嚴格、讀取寬鬆。</summary>
public class TaskBoardConfigTests
{
    private const string Valid = """{"version":1,"groupBy":"status","scope":"assigned"}""";

    [Fact]
    public void Default_IsStatusAssignedHideCancelled()
    {
        var d = TaskBoardConfig.Default;

        d.GroupBy.Should().Be("status");
        d.Scope.Should().Be("assigned");
        d.VisibleFixedColumns().Should().Equal("todo", "inProgress", "done");
        d.SortKey.Should().Be("dueDate");
        d.SortDescending.Should().BeFalse();
        TaskBoardConfig.Parse(d.Serialize()).Errors.Should().BeEmpty();
    }

    [Fact]
    public void Parse_UnknownProperty_Rejected()
    {
        TaskBoardConfig.Parse("""{"version":1,"groupBy":"status","scope":"assigned","groupby":"x"}""").Config.Should().BeNull();
        var (config, errors) = TaskBoardConfig.Parse("""{"version":1,"groupBy":"status","scope":"assigned","filters":{"keywrod":"a"}}""");
        config.Should().BeNull();
        errors.Should().ContainSingle().Which.Should().Contain("keywrod");
        TaskBoardConfig.Parse(Valid).Errors.Should().BeEmpty();
    }

    [Fact]
    public void Read_UnknownProperty_Ignored()
    {
        var c = TaskBoardConfig.Read("""{"version":1,"groupBy":"assignee","scope":"visible","showSubtasks":true,"filters":{"future":1,"keyword":"報價"}}""");

        c.GroupBy.Should().Be("assignee");
        c.Scope.Should().Be("visible");
        c.Filters!.Keyword.Should().Be("報價");
        TaskBoardConfig.Read("not json").Should().Be(TaskBoardConfig.Default);
        TaskBoardConfig.Read("""{"version":1,"groupBy":"kanban","scope":"nobody"}""").GroupBy.Should().Be("status");
    }

    [Theory]
    [InlineData("""{"version":1,"groupBy":"kanban","scope":"assigned"}""")]
    [InlineData("""{"version":1,"groupBy":"status","scope":"everyone"}""")]
    [InlineData("""{"version":1,"groupBy":"status","scope":"assigned","sort":{"key":"priority"}}""")]
    [InlineData("""{"version":1,"groupBy":"status","scope":"assigned","sort":{"key":"title","dir":"down"}}""")]
    [InlineData("""{"version":2,"groupBy":"status","scope":"assigned"}""")]
    [InlineData("""{"groupBy":"status","scope":"assigned"}""")]
    [InlineData("""{"version":1,"scope":"assigned"}""")]
    [InlineData("""{"version":1,"groupBy":"status","scope":"assigned","filters":{"statuses":["open"]}}""")]
    [InlineData("""{"version":1,"groupBy":"status","scope":"assigned","filters":{"sources":["mail"]}}""")]
    [InlineData("""{"version":1,"groupBy":"status","scope":"assigned","filters":{"dueRange":{"kind":"nextWeek"}}}""")]
    [InlineData("""{"version":1,"groupBy":"status","scope":"assigned","filters":{"dueRange":{"kind":"lastDays"}}}""")]
    [InlineData("""{"version":1,"groupBy":"status","scope":"assigned","columns":{"hidden":["overdue"]}}""")]
    [InlineData("""{"version":1,"groupBy":"status","scope":"assigned","groupBy2":1}""")]
    [InlineData("""{"version":"1","groupBy":"status","scope":"assigned"}""")]
    public void Rejects_BadGroupByScopeSort(string json)
    {
        var (config, errors) = TaskBoardConfig.Parse(json);

        config.Should().BeNull();
        errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Rejects_Over50ExplicitAssignees()
    {
        string Json(int n) => """{"version":1,"groupBy":"assignee","scope":"visible","columns":{"assignees":["""
            + string.Join(",", Enumerable.Range(1, n).Select(i => $"\"u{i}\"")) + "]}}";

        TaskBoardConfig.Parse(Json(50)).Errors.Should().BeEmpty();
        TaskBoardConfig.Parse(Json(51)).Errors.Should().ContainSingle().Which.Should().Contain("50");
    }

    [Fact]
    public void Rejects_KeywordOver100()
    {
        string Json(int n) => "{\"version\":1,\"groupBy\":\"status\",\"scope\":\"assigned\",\"filters\":{\"keyword\":\"" + new string('字', n) + "\"}}";

        TaskBoardConfig.Parse(Json(100)).Errors.Should().BeEmpty();
        TaskBoardConfig.Parse(Json(101)).Errors.Should().ContainSingle().Which.Should().Contain("100");
    }

    [Fact]
    public void DueBucket_IgnoresStatusFilter()
    {
        var due = TaskBoardConfig.Parse("""{"version":1,"groupBy":"dueBucket","scope":"assigned","filters":{"statuses":["done"]}}""").Config!;
        var status = TaskBoardConfig.Parse("""{"version":1,"groupBy":"status","scope":"assigned","filters":{"statuses":["done"]}}""").Config!;

        due.StatusFilter().Should().Equal(TaskItemStatus.Todo, TaskItemStatus.InProgress);
        status.StatusFilter().Should().Equal(TaskItemStatus.Done);
        due.VisibleFixedColumns().Should().Equal("overdue", "today", "thisWeek", "later", "none");
    }
}
