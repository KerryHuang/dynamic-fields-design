using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class ProgramRepository(DynamicDbContext db) : IProgramRepository
{
    private const string Prefix = "DYN";

    public async Task<string> NextModuleProgramCodeAsync(CancellationToken ct)
    {
        var codes = await db.Set<DynamicProgram>().AsNoTracking()
            .Where(p => p.ProgramCode.StartsWith(Prefix))
            .Select(p => p.ProgramCode).ToListAsync(ct);
        var max = codes
            .Select(c => int.TryParse(c.AsSpan(Prefix.Length), out var n) ? n : 0)
            .DefaultIfEmpty(0).Max();
        return $"{Prefix}{Math.Max(max, 0) + 1:D5}";
    }

    public Task<DynamicProgram?> GetAsync(string programCode, CancellationToken ct) =>
        db.Set<DynamicProgram>().FirstOrDefaultAsync(p => p.ProgramCode == programCode, ct);

    public void Add(DynamicProgram p) => db.Set<DynamicProgram>().Add(p);
}
