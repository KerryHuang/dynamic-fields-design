using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace DynamicModules.Persistence.Postgres;

/// <summary>
/// SaveChanges 自己開的交易（PostgreSQL provider 設計 §5.6）：
/// <list type="bullet">
/// <item>把 <c>AutoTransactionBehavior</c> 設成 Always（可寫死的技術限制，設計 §11 第 10 項）：只發一個指令的 SaveChanges 也開交易、
/// 經過全庫寫入鎖，不會插進持鎖交易的兩個陳述式之間。</item>
/// <item>沒有外層交易時先在 <see cref="WriteGate"/> 排隊（在開連線之前），儲存結束（成功、失敗、取消、並行衝突）時放開；
/// 有外層交易時閘門已由 <see cref="PostgresTransactionManager"/> 持有，不重複排隊。</item>
/// </list>
/// 註冊順序：放在其他 SaveChanges 攔截器之前（<see cref="PostgresDatabase.Configure"/>）；排在後面的攔截器若在 SavingChanges 丟例外，
/// EF 不會呼叫任何結束事件，這裡排到的閘門就放不掉。
/// </summary>
public sealed class PostgresSaveChangesGateInterceptor(PostgresWriteSettings settings) : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, IDisposable> _permits = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (Prepare(eventData.Context) is { } context)
            // 同步路徑同步等待即可；產品程式都走非同步
            Hold(context, WriteGate.ForKey(GateKey(context)).EnterAsync(settings.LockTimeout, CancellationToken.None).GetAwaiter().GetResult());
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Prepare(eventData.Context) is { } context)
            Hold(context, await WriteGate.ForKey(GateKey(context)).EnterAsync(settings.LockTimeout, cancellationToken));
        return result;
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Release(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Release(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Release(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Release(eventData.Context);
        return Task.CompletedTask;
    }

    public override void SaveChangesCanceled(DbContextEventData eventData) => Release(eventData.Context);

    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        Release(eventData.Context);
        return Task.CompletedTask;
    }

    // 並行衝突走另一條路，EF 不一定再呼叫 SaveChangesFailed；這時交易已經要回滾，提早放開閘門沒有關係
    public override InterceptionResult ThrowingConcurrencyException(ConcurrencyExceptionEventData eventData, InterceptionResult result)
    {
        Release(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
        ConcurrencyExceptionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Release(eventData.Context);
        return ValueTask.FromResult(result);
    }

    /// <summary>設定 Always；需要自己排隊（沒有外層交易）時回 context。</summary>
    private static DbContext? Prepare(DbContext? context)
    {
        if (context is null) return null;
        context.Database.AutoTransactionBehavior = AutoTransactionBehavior.Always;
        return context.Database.CurrentTransaction is null ? context : null;
    }

    internal static string GateKey(DbContext context) =>
        context.Database.GetConnectionString() ?? throw new InvalidOperationException("PostgreSQL 的 DbContext 沒有連線字串，無法決定寫入閘門");

    private void Hold(DbContext context, IDisposable permit)
    {
        // 上一次儲存若沒走到結束事件（理論上不會），先放掉舊的，避免閘門永遠被占住
        if (_permits.TryGetValue(context, out var stale)) stale.Dispose();
        _permits.AddOrUpdate(context, permit);
    }

    private void Release(DbContext? context)
    {
        if (context is null || !_permits.TryGetValue(context, out var permit)) return;
        _permits.Remove(context);
        permit.Dispose();
    }
}
