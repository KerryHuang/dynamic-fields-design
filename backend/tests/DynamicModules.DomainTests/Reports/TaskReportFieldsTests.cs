using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using F = DynamicModules.Domain.Reports.TaskReportFields;

namespace DynamicModules.DomainTests.Reports;

public class TaskReportFieldsTests
{
    private static I18nText Zh(string s) => new([KeyValuePair.Create("zh-TW", s)]);

    /// <summary>解析函式：只有繁中、值是鍵（文字本身由 Application 的快照驗，語系包 i4c 計畫判斷 9）。</summary>
    private static I18nText Text(string key) => new([KeyValuePair.Create("zh-TW", key)]);

    private static Module Build(string dataset = ReportDatasets.Tasks) => F.Build(dataset,
        [new Department { Id = 5, NameI18n = Zh("業務一"), Active = true }, new Department { Id = 6, NameI18n = Zh("舊部門"), Active = false }],
        [new Module { Id = 11, Key = "order", NameI18n = Zh("訂單") }, new Module { Id = 12, Key = "leave", NameI18n = Zh("請假單") }],
        [new TaskRule { Id = 21, ModuleId = 11, NameI18n = Zh("出貨提醒") }], Text);

    private static IReadOnlyList<Field> Fields(Module m) => m.Blocks.SelectMany(b => b.Fields).ToList();

    private static Field Of(Module m, long id) => Fields(m).Single(f => f.Id == id);

    [Fact]
    public void Build_Tasks_FieldsNegativeAndTyped()
    {
        var m = Build();
        m.Id.Should().Be(0);
        m.Key.Should().BeEmpty();
        Fields(m).Select(f => f.Id).Should().BeEquivalentTo(Enumerable.Range(1, 16).Select(i => (long)-i));
        F.Selectable(ReportDatasets.Tasks).Should().BeEquivalentTo(Enumerable.Range(1, 16).Select(i => (long)-i));
        Of(m, F.Title).Type.Should().Be(FieldType.Textarea);
        Of(m, F.Overdue).Type.Should().Be(FieldType.Boolean);
        Of(m, F.Assignee).Type.Should().Be(FieldType.User);
        Of(m, F.DueDate).Type.Should().Be(FieldType.Date);
        Of(m, F.CompletedTime).Type.Should().Be(FieldType.DateTime);
        Of(m, F.DelayDays).Type.Should().Be(FieldType.Integer);
        Of(m, F.Module).Type.Should().Be(FieldType.Enum);
        Fields(m).Should().OnlyContain(f => f.Status == FieldStatus.Active);
        m.NameI18n.Resolve("zh-TW").Should().Be(F.DatasetNameKeys[ReportDatasets.Tasks]);
    }

    [Fact]
    public void Build_TaskEvents_ContainsSelectableAndDrill()
    {
        var m = Build(ReportDatasets.TaskEvents);
        var ids = Fields(m).Select(f => f.Id).ToHashSet();
        ids.Should().Contain(F.Selectable(ReportDatasets.TaskEvents)).And.Contain(F.DrillColumns(ReportDatasets.TaskEvents));
        ids.Should().Contain([F.EventKind, F.EventDate]);
        ids.Should().NotContain([F.Overdue, F.Completion, F.CompletionDays, F.DelayDays]);
        F.Selectable(ReportDatasets.TaskEvents).Should().NotContain(F.Title);
        Of(m, F.EventKind).Type.Should().Be(FieldType.Enum);
        Of(m, F.EventDate).Type.Should().Be(FieldType.DateTime);
    }

    [Fact]
    public void Build_Options_FromEntities()
    {
        var m = Build();
        Of(m, F.AssigneeDepartment).Options.Select(o => (o.Id, o.Active)).Should().BeEquivalentTo(new[] { (5L, true), (6L, false) });
        Of(m, F.Module).Options.Select(o => o.Id).Should().BeEquivalentTo(new long[] { 11, 12 });
        Of(m, F.Module).Options.Single(o => o.Id == 12).LabelI18n.Resolve("zh-TW").Should().Be("請假單");
        Of(m, F.Rule).Options.Select(o => o.Id).Should().BeEquivalentTo(new long[] { 21 });
        Of(m, F.Status).Options.Select(o => o.Id).Should().BeEquivalentTo(new long[] { 1, 2, 3, 4 });
        F.StatusOption(TaskItemStatus.Todo).Should().Be(1);
        F.StatusOption(TaskItemStatus.Cancelled).Should().Be(4);
        Of(m, F.Source).Options.Select(o => o.Id).Should().BeEquivalentTo(new[] { F.SourcePersonal, F.SourceRecord, F.SourceRule });
        Of(m, F.Completion).Options.Select(o => o.Id).Should().BeEquivalentTo(new[] { F.CompletionOnTime, F.CompletionLate, F.CompletionNoDue, F.CompletionNotDone });
        foreach (var id in new[] { F.Status, F.Source, F.Completion, F.Module, F.Rule })
            Of(m, id).Options.Should().OnlyContain(o => o.Active);
        Of(Build(ReportDatasets.TaskEvents), F.EventKind).Options.Should().OnlyContain(o => o.Active)
            .And.HaveCount(2);
    }

    [Fact]
    public void Title_CannotGroup() => ReportFieldEligibility.CanGroup(Of(Build(), F.Title)).Should().BeFalse();

    [Fact]
    public void Labels_FromResolver()
    {
        var fields = Fields(Build()).Concat(Fields(Build(ReportDatasets.TaskEvents))).ToList();
        var keys = F.AllTextKeys.ToHashSet();
        foreach (var f in fields)
        {
            keys.Should().Contain(f.LabelI18n.Resolve("zh-TW"), $"欄位 {f.Id} 的標籤來自解析函式");
            if (f.Id is F.AssigneeDepartment or F.Module or F.Rule)
                f.Options.Should().OnlyContain(o => !keys.Contains(o.LabelI18n.Resolve("zh-TW")), $"欄位 {f.Id} 的選項是實體名稱，原樣帶過");
            else
                foreach (var o in f.Options) keys.Should().Contain(o.LabelI18n.Resolve("zh-TW"), $"欄位 {f.Id} 選項 {o.Id} 的標籤來自解析函式");
        }
        Of(Build(), F.Status).LabelI18n.Resolve("zh-TW").Should().Be(F.TextKeys.FieldStatus);
        Of(Build(), F.Status).Options.Select(o => o.LabelI18n.Resolve("zh-TW"))
            .Should().Equal(F.TextKeys.StatusTodo, F.TextKeys.StatusInProgress, F.TextKeys.StatusDone, F.TextKeys.StatusCancelled);
        F.BlankLabelKeys.Keys.Should().BeEquivalentTo(new[] { F.Assignee, F.AssigneeDepartment, F.Creator, F.Module, F.Rule });
        F.BlankLabelKeys[F.Assignee].Should().Be(F.TextKeys.BlankAssignee);
        F.Restrictable.Should().BeEquivalentTo(new[] { F.Module, F.Rule });
    }

    [Fact]
    public void DrillColumns_ByDataset()
    {
        F.DrillColumns(ReportDatasets.Tasks).Should().Equal(F.Title, F.Status, F.Assignee, F.DueDate, F.CompletedTime, F.Module);
        F.DrillColumns(ReportDatasets.TaskEvents).Should().Equal(F.EventKind, F.EventDate, F.Title, F.Status, F.Assignee, F.Module);
    }

    [Fact]
    public void Datasets_ModuleAndTask()
    {
        ReportDatasets.IsModule(null).Should().BeTrue();
        ReportDatasets.IsModule("module").Should().BeTrue();
        ReportDatasets.IsModule("tasks").Should().BeFalse();
        ReportDatasets.IsTask("tasks").Should().BeTrue();
        ReportDatasets.IsTask("taskEvents").Should().BeTrue();
        ReportDatasets.IsTask("foo").Should().BeFalse();
        ReportDatasets.IsTask(null).Should().BeFalse();
    }
}
