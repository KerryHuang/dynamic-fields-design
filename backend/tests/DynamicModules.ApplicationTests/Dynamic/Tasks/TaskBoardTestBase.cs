using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Application.Dynamic.TaskService;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>看板欄查詢與移動測試共用：可調的營運時鐘（預設 2026-11-04 週三，一週起始日出廠是週一 → 本週最後一天 11-08）。</summary>
public abstract class TaskBoardTestBase : TaskViewTestBase
{
    protected sealed class Clock : IBusinessTime
    {
        public DateTime Now { get; set; } = new(2026, 11, 4, 9, 0, 0);
        public DateOnly Today => DateOnly.FromDateTime(Now);
    }

    protected readonly Clock BusinessClock = new();

    protected static readonly DateOnly Today = new(2026, 11, 4);

    protected override void ConfigureServices(IServiceCollection services) => services.AddSingleton<IBusinessTime>(BusinessClock);

    protected async Task<TaskBoardResponse> Board(string user, string? config = null, long? viewId = null)
    {
        var r = await SendAs(user, new GetTaskBoardColumnsRequest(viewId, config is null ? null : J(config)));
        r.IsSuccess.Should().BeTrue(r.Message + string.Join(";", r.Errors.SelectMany(e => e.Value)));
        return (TaskBoardResponse)r.Data!;
    }

    protected static TaskBoardColumnResponse Col(TaskBoardResponse b, string key) => b.Columns.Single(c => c.Key == key);

    protected Task<ServiceResult> SetWeekStart(string day) => Send(new UpdateSystemPolicyRequest("calendar.weekStart", J($"\"{day}\"")));
}
