using DynamicModules.Application.Dynamic.NotificationService;
using DynamicModules.Application.Dynamic.TaskService;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>
/// 包住真正的任務通知寫入端；FailWhen 對這批變更的任一筆成立時丟例外，模擬連動寫入途中失敗（看板設計 §10.7.4 第 10 項）。
/// 通知在任務第一次 Commit 之後才送，所以丟例外時任務的變更已寫進交易，用來確認整筆回滾。
/// </summary>
public sealed class FailingTaskNotifier(TaskNotificationWriter inner, FailingTaskNotifier.Switch control) : ITaskNotifier
{
    public sealed class Switch
    {
        public Func<TaskChange, bool>? FailWhen { get; set; }
    }

    public static void Register(IServiceCollection services, Switch control)
    {
        services.AddSingleton(control);
        services.AddScoped<TaskNotificationWriter>();
        services.AddScoped<ITaskNotifier, FailingTaskNotifier>();
    }

    public Task NotifyAsync(IReadOnlyList<TaskChange> changes, CancellationToken ct) =>
        control.FailWhen is { } fail && changes.Any(fail)
            ? throw new InvalidOperationException("模擬連動途中失敗")
            : inner.NotifyAsync(changes, ct);
}
