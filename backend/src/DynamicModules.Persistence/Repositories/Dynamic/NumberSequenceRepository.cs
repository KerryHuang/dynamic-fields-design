using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class NumberSequenceRepository(DynamicDbContext db) : INumberSequenceRepository
{
    public async Task<long> NextAsync(long moduleId, long fieldId, string period, CancellationToken ct)
    {
        // 同一次存檔可能連續取號（表頭的多列明細）：先看這個 context 裡還沒存檔的序號列
        var seq = db.Set<NumberSequence>().Local.FirstOrDefault(s => s.FieldId == fieldId && s.Period == period)
            ?? await db.Set<NumberSequence>().FirstOrDefaultAsync(s => s.FieldId == fieldId && s.Period == period, ct);
        if (seq is null)
        {
            db.Set<NumberSequence>().Add(new NumberSequence
                { ModuleId = moduleId, FieldId = fieldId, Period = period, NextValue = 2 });
            return 1;
        }
        return seq.NextValue++;
    }
}
