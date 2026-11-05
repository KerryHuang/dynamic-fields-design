using Dapper;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Persistence.Dynamic.Dialects;
using DynamicModules.Persistence.Dynamic.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class RecordRepository(DynamicDbContext db, ISqlDialect dialect) : IRecordRepository
{
    public Task<Record?> GetAsync(long id, CancellationToken ct) =>
        db.Set<Record>().FirstOrDefaultAsync(r => r.Id == id && !r.DeleteFlag, ct);

    public Task<List<Record>> ListAllByModuleAsync(long moduleId, CancellationToken ct) =>
        db.Set<Record>().Where(r => r.ModuleId == moduleId).ToListAsync(ct);

    public Task<bool> AnyByModuleAsync(long moduleId, CancellationToken ct) =>
        db.Set<Record>().AnyAsync(r => r.ModuleId == moduleId, ct);

    public Task<Record?> GetByHostAsync(long moduleId, string hostKey, CancellationToken ct) =>
        db.Set<Record>().FirstOrDefaultAsync(r => r.ModuleId == moduleId && r.HostKey == hostKey && !r.DeleteFlag, ct);

    public void Add(Record r) => db.Set<Record>().Add(r);

    public async Task<IReadOnlyList<Record>> ListForReportAsync(
        long moduleId, IReadOnlyCollection<string>? ownerUserIds, int limit, CancellationToken ct)
    {
        var query = db.Set<Record>().AsNoTracking().Where(r => r.ModuleId == moduleId && !r.DeleteFlag);
        if (ownerUserIds is not null) query = query.Where(r => ownerUserIds.Contains(r.OwnerUserId));
        return await query.OrderBy(r => r.Id).Take(limit).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Record>> ListByIdsAsync(long moduleId, IReadOnlyCollection<long> ids, CancellationToken ct) =>
        await db.Set<Record>().AsNoTracking().Where(r => r.ModuleId == moduleId && !r.DeleteFlag && ids.Contains(r.Id)).ToListAsync(ct);

    public async Task<IReadOnlySet<long>> ListVisibleIdsAsync(
        long moduleId, IReadOnlyCollection<long> ids, IReadOnlyCollection<string>? ownerUserIds, CancellationToken ct)
    {
        var query = db.Set<Record>().Where(r => r.ModuleId == moduleId && !r.DeleteFlag && ids.Contains(r.Id));
        if (ownerUserIds is not null) query = query.Where(r => ownerUserIds.Contains(r.OwnerUserId));
        return (await query.Select(r => r.Id).ToListAsync(ct)).ToHashSet();
    }

    public async Task<IReadOnlyList<RecordStub>> ListExistingAsync(long moduleId, IReadOnlyCollection<long> ids, CancellationToken ct) =>
        await db.Set<Record>().Where(r => r.ModuleId == moduleId && !r.DeleteFlag && ids.Contains(r.Id))
            .Select(r => new RecordStub(r.Id, r.Title, r.OwnerUserId)).ToListAsync(ct);

    public async Task<(IReadOnlyList<RecordStub> items, int total)> QueryLightAsync(
        long moduleId, string? keyword, IReadOnlyCollection<string>? ownerUserIds, int page, int size, CancellationToken ct)
    {
        var query = db.Set<Record>().Where(r => r.ModuleId == moduleId && !r.DeleteFlag);
        if (ownerUserIds is not null) query = query.Where(r => ownerUserIds.Contains(r.OwnerUserId));
        if (keyword is not null) query = query.Where(r => r.Title.Contains(keyword));
        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(r => r.Title).ThenBy(r => r.Id).Skip((page - 1) * size).Take(size)
            .Select(r => new RecordStub(r.Id, r.Title, r.OwnerUserId)).ToListAsync(ct);
        return (items, total);
    }

    public async Task<(List<Record> items, int total)> QueryAsync(RecordQuery q, CancellationToken ct)
    {
        var (countSql, pageSql, parameters) = DynamicQueryBuilder.Build(q, dialect);
        var connection = db.Database.GetDbConnection();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(countSql, parameters, transaction, cancellationToken: ct));
            var items = (await connection.QueryAsync<Record>(new CommandDefinition(pageSql, parameters, transaction, cancellationToken: ct))).ToList();
            return (items, total);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public async Task<(IReadOnlyList<Record> Items, int Total)> QueryByRefAsync(
        long fieldId, string refKey, IReadOnlyCollection<string>? ownerUserIds, int page, int size, CancellationToken ct)
    {
        var query = db.Set<Record>().AsNoTracking().Where(r => !r.DeleteFlag
            && db.Set<RecordIndex>().Any(x => x.RecordId == r.Id && x.FieldId == fieldId && x.RefKey == refKey));
        if (ownerUserIds is not null) query = query.Where(r => ownerUserIds.Contains(r.OwnerUserId));
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(r => r.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return (items, total);
    }

    public async Task<IReadOnlyList<Record>> ListByRefAsync(long fieldId, string refKey, CancellationToken ct) =>
        await db.Set<Record>().Where(r => !r.DeleteFlag
                && db.Set<RecordIndex>().Any(x => x.RecordId == r.Id && x.FieldId == fieldId && x.RefKey == refKey))
            .OrderBy(r => r.Id).ToListAsync(ct);

    public async Task<IReadOnlyList<long>> ListDetailRowIdsAsync(long detailFieldId, string parentKey, CancellationToken ct) =>
        await db.Set<RecordIndex>().Where(x => x.FieldId == detailFieldId && x.RefKey == parentKey)
            .Select(x => x.RecordId).Distinct().ToListAsync(ct);

    public async Task RemoveFieldIndexAsync(long recordId, long fieldId, CancellationToken ct)
    {
        await db.Set<RecordIndex>().Where(x => x.RecordId == recordId && x.FieldId == fieldId).ExecuteDeleteAsync(ct);
        Detach(x => x.RecordId == recordId && x.FieldId == fieldId);
    }

    public async Task ReplaceIndexAsync(Record record, IReadOnlyList<RecordIndex> rows, CancellationToken ct)
    {
        if (record.Id == 0)
        {
            record.Index = rows.ToList();
            return;
        }
        await db.Set<RecordIndex>().Where(x => x.RecordId == record.Id).ExecuteDeleteAsync(ct);
        Detach(x => x.RecordId == record.Id);
        foreach (var row in rows) row.RecordId = record.Id;
        db.Set<RecordIndex>().AddRange(rows);
    }

    public async Task ReplaceFieldIndexAsync(long fieldId, IReadOnlyList<RecordIndex> rows, CancellationToken ct)
    {
        await db.Set<RecordIndex>().Where(x => x.FieldId == fieldId).ExecuteDeleteAsync(ct);
        Detach(x => x.FieldId == fieldId);
        db.Set<RecordIndex>().AddRange(rows);
    }

    /// <summary>ExecuteDelete 繞過 change tracker；同一個 context 內已追蹤的舊列要移除，否則重新加入會鍵衝突。</summary>
    private void Detach(Func<RecordIndex, bool> match)
    {
        foreach (var e in db.ChangeTracker.Entries<RecordIndex>().Where(e => match(e.Entity)).ToList())
            e.State = EntityState.Detached;
    }
}
