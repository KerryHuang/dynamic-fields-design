using DynamicModules.Domain.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace DynamicModules.Persistence.Postgres;

/// <summary>
/// SaveChanges 自己開的交易（PostgreSQL provider 設計 §5.6）：
/// <list type="bullet">
/// <item>把 <c>AutoTransactionBehavior</c> 設成 Always（可寫死的技術限制，設計 §11 第 10 項）：只發一個指令的 SaveChanges 也開交易、
/// 經過全庫寫入鎖，不會插進持鎖交易的兩個陳述式之間。</item>
/// <item>沒有外層交易時先在 <see cref="WriteGate"/> 排隊（在開連線之前），排到的寫入權交給 <see cref="DynamicDbContext.HoldUntilSaveEnds"/>，
/// 由覆寫的 SaveChanges 在 finally 放開；有外層交易時閘門已由 <see cref="PostgresTransactionManager"/> 持有，不重複排隊。</item>
/// </list>
/// 不靠 EF 的結束事件放開：排在後面的攔截器在 <c>SavingChanges</c> 丟例外、或 <c>DetectChanges</c> 丟例外時，EF 不發任何結束事件。
/// 因此攔截器的註冊順序不影響釋放。
/// </summary>
public sealed class PostgresSaveChangesGateInterceptor(PostgresWriteSettings settings) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (Prepare(eventData.Context) is { } context)
            // 同步路徑同步等待即可；產品程式都走非同步
            context.HoldUntilSaveEnds(WriteGate.ForKey(GateKey(context)).EnterAsync(settings.LockTimeout, CancellationToken.None).GetAwaiter().GetResult());
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Prepare(eventData.Context) is { } context)
            context.HoldUntilSaveEnds(await WriteGate.ForKey(GateKey(context)).EnterAsync(settings.LockTimeout, cancellationToken));
        return result;
    }

    /// <summary>設定 Always；需要自己排隊（沒有外層交易）時回 context。</summary>
    private static DynamicDbContext? Prepare(DbContext? context)
    {
        if (context is null) return null;
        context.Database.AutoTransactionBehavior = AutoTransactionBehavior.Always;
        if (context.Database.CurrentTransaction is not null) return null;
        return context as DynamicDbContext
            ?? throw new InvalidOperationException($"PostgreSQL 的 SaveChanges 閘門只支援 {nameof(DynamicDbContext)}（要靠它的 SaveChanges 放開閘門）");
    }

    internal static string GateKey(DbContext context) =>
        context.Database.GetConnectionString() ?? throw new InvalidOperationException("PostgreSQL 的 DbContext 沒有連線字串，無法決定寫入閘門");
}
