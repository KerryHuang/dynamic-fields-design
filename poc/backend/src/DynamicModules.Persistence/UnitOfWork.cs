using DynamicModules.Application.Common;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence;

public class UnitOfWork(DynamicDbContext db, IWriteJournal journal) : IUnitOfWork
{
    /// <summary>
    /// 資料庫忙碌（SQLITE_BUSY／LOCKED，多半是別的行程占著寫入鎖）轉成可重試的 <see cref="DatabaseBusyException"/>（API 回 503），
    /// 而不是未處理的例外（500）。
    /// 儲存成功後把這次儲存對記錄的增量寫進請求的寫入日誌（設計 §3.3）；新增的記錄要等儲存後才有 Id。
    /// </summary>
    public async Task CommitAsync(CancellationToken ct)
    {
        var pending = db.ChangeTracker.Entries<Record>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .Select(e => new Pending(
                e.Entity, e.State == EntityState.Added ? JournalKind.Added : JournalKind.Modified,
                e.State == EntityState.Added ? null : (string)e.OriginalValues[nameof(Record.ValuesJson)]!,
                e.State == EntityState.Added ? false : (bool)e.OriginalValues[nameof(Record.DeleteFlag)]!,
                journal.IsMarkedSilent(e.Entity)))
            .ToList();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException && DatabaseBusy.Is(ex))
        {
            throw new DatabaseBusyException(DatabaseBusyException.DefaultMessage, ex);
        }
        foreach (var p in pending)
            journal.Add(seq => new JournalEntry(
                seq, p.Entity.Id, p.Entity.ModuleId, p.Kind, p.BeforeValues, p.BeforeDeleted,
                p.Entity.ValuesJson, p.Entity.DeleteFlag, p.Silent, p.Entity.OwnerUserId));
    }

    private sealed record Pending(Record Entity, JournalKind Kind, string? BeforeValues, bool BeforeDeleted, bool Silent);
}
