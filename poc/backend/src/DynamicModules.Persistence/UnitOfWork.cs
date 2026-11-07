using DynamicModules.Application.Common;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence;

public class UnitOfWork(DynamicDbContext db) : IUnitOfWork
{
    /// <summary>
    /// 資料庫忙碌（SQLITE_BUSY／LOCKED，多半是別的行程占著寫入鎖）轉成可重試的 <see cref="DatabaseBusyException"/>（API 回 503），
    /// 而不是未處理的例外（500）。
    /// </summary>
    public async Task CommitAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException && DatabaseBusy.Is(ex))
        {
            throw new DatabaseBusyException(DatabaseBusyException.DefaultMessage, ex);
        }
    }
}
