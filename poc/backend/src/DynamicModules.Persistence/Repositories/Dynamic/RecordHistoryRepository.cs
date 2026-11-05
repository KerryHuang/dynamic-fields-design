using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class RecordHistoryRepository(DynamicDbContext db) : IRecordHistoryRepository
{
    public void Add(RecordHistory h) => db.Set<RecordHistory>().Add(h);

    public async Task<(IReadOnlyList<RecordHistory> Items, int Total)> QueryAsync(
        IReadOnlyCollection<long> recordIds, IReadOnlyCollection<long> hiddenFieldIds, int page, int size, CancellationToken ct)
    {
        var query = db.Set<RecordHistory>().AsNoTracking().Where(h => recordIds.Contains(h.RecordId)
            && (h.FieldId == null || !hiddenFieldIds.Contains(h.FieldId.Value)));
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(h => h.ChangedTime).ThenByDescending(h => h.Id)
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return (items, total);
    }
}
