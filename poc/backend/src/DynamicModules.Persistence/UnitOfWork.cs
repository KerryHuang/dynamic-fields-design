using DynamicModules.Domain.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence;

public class UnitOfWork(DynamicDbContext db) : IUnitOfWork
{
    /// <summary>
    /// SQLite 的寫入鎖被別的連線占著時（SQLITE_BUSY／LOCKED，讀取後升級成寫入的交易會立刻失敗而不等待），
    /// 本質上就是兩個寫入者同時競爭：轉成 DbUpdateConcurrencyException，和 SQL Server 上 concurrency token 擋下後寫入者一樣回 409，而不是 500。
    /// </summary>
    public async Task CommitAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException && ex.InnerException is SqliteException { SqliteErrorCode: 5 or 6 })
        {
            throw new DbUpdateConcurrencyException("資料庫忙碌，寫入被其他交易搶先", ex);
        }
    }
}
