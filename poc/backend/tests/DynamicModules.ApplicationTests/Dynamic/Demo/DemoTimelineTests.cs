using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Acting;
using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

public class DemoTimelineTests
{
    private static readonly DateOnly Today = DemoTestHost.Today;

    private static DemoContext EmptyContext() => new(Today, null!,
        new TemplateIdMap(new Dictionary<string, long>(), new Dictionary<string, string>(), new Dictionary<string, long>(),
            new Dictionary<string, long>(), new Dictionary<string, string>()),
        new Dictionary<string, long>());

    [Fact]
    public void Plan_NoStepOnOrAfterToday()
    {
        var steps = DemoTimeline.Plan(Today, DemoCatalog.Scenarios);

        steps.Should().NotBeEmpty();
        steps.Should().OnlyContain(s => s.At < Today.ToDateTime(TimeOnly.MinValue));
        steps.Should().OnlyContain(s => s.At >= Today.AddDays(-180).ToDateTime(TimeOnly.MinValue));
        steps.Should().OnlyContain(s => s.At.Hour >= 8 && s.At.Hour <= 17);
    }

    [Fact]
    public void Plan_SameTodayAndSeed_SameSteps()
    {
        var a = DemoTimeline.Plan(Today, DemoCatalog.Scenarios).Select(s => (s.At, s.UserId, s.Label));
        var b = DemoTimeline.Plan(Today, DemoCatalog.Scenarios).Select(s => (s.At, s.UserId, s.Label));

        a.Should().Equal(b);
    }

    [Fact]
    public void At_AfterOnLaterDay_IsAfterAfter()
    {
        var plan = new DemoPlan(Today, new Random(1), new Dictionary<string, IReadOnlyList<DemoRecord>>());
        var date = Today.AddDays(-10);

        for (var i = 0; i < 200; i++)
        {
            var after = date.AddDays(2).ToDateTime(new TimeOnly(8 + i % 10, i % 60));
            var at = plan.At(date, after);
            at.Should().BeAfter(after);
            at.Hour.Should().BeInRange(8, 17);
        }
    }

    [Fact]
    public async Task Run_SortsByTimeThenInsertion()
    {
        var day = Today.AddDays(-3);
        var ran = new List<(string Label, string? User, DateTime? Now)>();
        DemoStep Step(int hour, string label) => new(day.ToDateTime(new TimeOnly(hour, 0)), $"u-{label}", label, (_, _) =>
        {
            ran.Add((label, ActingScope.UserId, ActingScope.Now));
            return Task.FromResult(ServiceResult.Success());
        });
        var progress = new List<(int, int)>();

        var failed = await DemoTimeline.RunAsync(EmptyContext(),
            [Step(10, "a"), Step(9, "b"), Step(10, "c"), Step(8, "d")], new SyncProgress(progress), CancellationToken.None);

        failed.Should().BeNull();
        ran.Select(r => r.Label).Should().Equal("d", "b", "a", "c");
        ran[0].User.Should().Be("u-d");
        ran[0].Now.Should().Be(day.ToDateTime(new TimeOnly(8, 0)));
        ActingScope.UserId.Should().BeNull("每步結束就還原身分");
        progress.Should().Equal((1, 4), (2, 4), (3, 4), (4, 4));
    }

    [Fact]
    public async Task Run_StopsAtFirstFailure_LabelInMessage()
    {
        var at = Today.AddDays(-1).ToDateTime(new TimeOnly(9, 0));
        var ran = new List<string>();
        DemoStep Step(string label, ServiceResult result) => new(at, "admin", label, (_, _) =>
        {
            ran.Add(label);
            return Task.FromResult(result);
        });

        var failed = await DemoTimeline.RunAsync(EmptyContext(),
        [
            Step("第一步", ServiceResult.Success()),
            Step("第二步", ServiceResult.Invalid(new Dictionary<string, string[]> { ["f_1"] = ["此欄位必填"] })),
            Step("第三步", ServiceResult.Success())
        ], null, CancellationToken.None);

        ran.Should().Equal("第一步", "第二步");
        failed!.IsSuccess.Should().BeFalse();
        failed.Errors["demo"].Should().Equal("第二步：f_1: 此欄位必填");
    }

    [Fact]
    public async Task Run_ConflictFailure_UsesMessage()
    {
        var at = Today.AddDays(-1).ToDateTime(new TimeOnly(9, 0));

        var failed = await DemoTimeline.RunAsync(EmptyContext(),
            [new DemoStep(at, "admin", "改訂單", (_, _) => Task.FromResult(ServiceResult.Conflict("資料已被其他人修改")))],
            null, CancellationToken.None);

        failed!.Errors["demo"].Should().Equal("改訂單：資料已被其他人修改");
    }

    /// <summary>Progress&lt;T&gt; 會丟到同步內容非同步回報，測試改用同步版。</summary>
    private sealed class SyncProgress(List<(int, int)> into) : IProgress<(int, int)>
    {
        public void Report((int, int) value) => into.Add(value);
    }
}
