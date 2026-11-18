using DynamicModules.Application.Dynamic.NotificationService;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>
/// 包住真正的任務通知寫入端；FailWhen 對這批變更的任一筆成立時丟例外，模擬連動寫入途中失敗（看板設計 §10.7.4 第 10 項）。
/// 通知在任務第一次 Commit 之後才送，所以丟例外時任務的變更已寫進交易，用來確認整筆回滾。
/// FailAfterSave 為 true 時先讓真正的寫入端寫通知並 SaveChanges，記下交易內看得到的通知筆數（SavedBeforeFailure），
/// 再丟例外：用來確認已存進交易的通知也一起回滾。
/// </summary>
public sealed class FailingTaskNotifier(TaskNotificationWriter inner, IUnitOfWork uow, DynamicDbContext db, FailingTaskNotifier.Switch control)
    : ITaskNotifier
{
    public sealed class Switch
    {
        public Func<TaskChange, bool>? FailWhen { get; set; }
        public bool FailAfterSave { get; set; }
        public int SavedBeforeFailure { get; set; }
    }

    public static void Register(IServiceCollection services, Switch control)
    {
        services.AddSingleton(control);
        services.AddScoped<TaskNotificationWriter>();
        services.AddScoped<ITaskNotifier, FailingTaskNotifier>();
    }

    public async Task NotifyAsync(IReadOnlyList<TaskChange> changes, CancellationToken ct)
    {
        if (control.FailWhen is not { } fail || !changes.Any(fail))
        {
            await inner.NotifyAsync(changes, ct);
            return;
        }
        if (control.FailAfterSave)
        {
            await inner.NotifyAsync(changes, ct);
            await uow.CommitAsync(ct);
            control.SavedBeforeFailure = await db.Set<Notification>().AsNoTracking().CountAsync(ct);
        }
        throw new InvalidOperationException("模擬連動途中失敗");
    }
}
