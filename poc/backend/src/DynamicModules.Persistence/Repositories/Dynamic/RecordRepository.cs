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

    public async Task<IReadOnlyList<RecordValues>> ListLiveValuesAsync(long moduleId, CancellationToken ct) =>
        await db.Set<Record>().AsNoTracking().Where(r => r.ModuleId == moduleId && !r.DeleteFlag).OrderBy(r => r.Id)
            .Select(r => new RecordValues(r.Id, r.ValuesJson)).ToListAsync(ct);

    public Task<bool> AnyByModuleAsync(long moduleId, CancellationToken ct) =>
        db.Set<Record>().AnyAsync(r => r.ModuleId == moduleId, ct);

    public Task<int> CountByModuleAsync(long moduleId, CancellationToken ct) =>
        db.Set<Record>().CountAsync(r => r.ModuleId == moduleId && !r.DeleteFlag, ct);

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

    public Task<List<Record>> ListBatchByModuleAsync(long moduleId, long afterId, int take, CancellationToken ct) =>
        db.Set<Record>().Where(r => r.ModuleId == moduleId && !r.DeleteFlag && r.Id > afterId).OrderBy(r => r.Id).Take(take).ToListAsync(ct);

    public async Task ClearFieldIndexAsync(long fieldId, CancellationToken ct)
    {
        await db.Set<RecordIndex>().Where(x => x.FieldId == fieldId).ExecuteDeleteAsync(ct);
        Detach(x => x.FieldId == fieldId);
    }

    public void AddIndexRows(IReadOnlyList<RecordIndex> rows) => db.Set<RecordIndex>().AddRange(rows);

    public void ReleaseTracked(IReadOnlyList<Record> batch)
    {
        var ids = batch.Select(r => r.Id).ToHashSet();
        foreach (var e in db.ChangeTracker.Entries<RecordIndex>().Where(e => ids.Contains(e.Entity.RecordId)).ToList()) e.State = EntityState.Detached;
        foreach (var r in batch) db.Entry(r).State = EntityState.Detached;
    }

    public async Task<bool> NeedsScaledBackfillAsync(long fieldId, int scale, CancellationToken ct)
    {
        if (await db.Set<RecordIndex>().AnyAsync(x => x.FieldId == fieldId && x.NumberValue != null && x.ScaledValue == null, ct)) return true;
        // 逐列比對 ScaledValue 與依 NumberValue 重算的值，全程用 decimal、要求完全相等：
        // 欄位值只有 15 位有效數字，double 轉 decimal 後正好是原值，不會像 double 乘法那樣在大數值上失準而被迫略過。
        // 誤判（多回填一次）無害；漏判會讓舊小數位的 ScaledValue 被加總，所以不設容差、不跳過任何列。
        var factor = 1m;
        for (var i = 0; i < scale; i++) factor *= 10m;
        var rows = db.Set<RecordIndex>().AsNoTracking()
            .Where(x => x.FieldId == fieldId && x.NumberValue != null && x.ScaledValue != null)
            .Select(x => new { x.NumberValue, x.ScaledValue });
        await foreach (var r in rows.AsAsyncEnumerable().WithCancellation(ct))
        {
            decimal expected;
            try
            {
                expected = Math.Round((decimal)r.NumberValue!.Value * factor, 0, MidpointRounding.AwayFromZero);
            }
            catch (OverflowException)
            {
                return true;   // 放大後連 decimal 都放不下，索引不可能是依這個小數位算的
            }
            if (expected != r.ScaledValue!.Value) return true;
        }
        return false;
    }

    private sealed record AggregateRow(long Count, long ScaledSum, long MissingScaled);

    public async Task<AggregateResult> AggregateAsync(AggregateQuery query, CancellationToken ct)
    {
        var (sql, parameters) = DynamicQueryBuilder.BuildAggregate(query, dialect);
        var connection = db.Database.GetDbConnection();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var row = await connection.QuerySingleAsync<AggregateRow>(new CommandDefinition(sql, parameters, transaction, cancellationToken: ct));
            return new AggregateResult(row.Count, row.ScaledSum, row.MissingScaled);
        }
        catch (Microsoft.Data.Sqlite.SqliteException e) when (e.Message.Contains("integer overflow", StringComparison.OrdinalIgnoreCase))
        {
            throw new AggregateOverflowException(e);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
