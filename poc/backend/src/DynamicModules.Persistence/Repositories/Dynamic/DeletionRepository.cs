using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class DeletionRepository(DynamicDbContext db) : IDeletionRepository
{
    public Task<List<Module>> ListModulesAsync(CancellationToken ct) =>
        db.Set<Module>().AsNoTracking().Include(m => m.Blocks).ThenInclude(b => b.Fields).ThenInclude(f => f.Options)
            .Include(m => m.Rules).Include(m => m.Workflow).OrderBy(m => m.Id).ToListAsync(ct);

    public Task<List<ListView>> ListViewsAsync(CancellationToken ct) =>
        db.Set<ListView>().AsNoTracking().OrderBy(v => v.Id).ToListAsync(ct);

    public Task<List<Report>> ListReportsAsync(CancellationToken ct) =>
        db.Set<Report>().AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct);

    public Task<List<Conversion>> ListConversionsAsync(CancellationToken ct) =>
        db.Set<Conversion>().AsNoTracking().OrderBy(c => c.Id).ToListAsync(ct);

    public Task<List<ModuleEvent>> ListEventsAsync(CancellationToken ct) =>
        db.Set<ModuleEvent>().AsNoTracking().OrderBy(e => e.Id).ToListAsync(ct);

    public Task<List<ModuleCheck>> ListChecksAsync(CancellationToken ct) =>
        db.Set<ModuleCheck>().AsNoTracking().OrderBy(c => c.Id).ToListAsync(ct);

    public Task<List<ModuleLedger>> ListLedgersAsync(CancellationToken ct) =>
        db.Set<ModuleLedger>().AsNoTracking().OrderBy(l => l.Id).ToListAsync(ct);

    public async Task<List<DashboardLayoutSource>> ListDashboardLayoutsAsync(CancellationToken ct)
    {
        var dashboards = await db.Set<Dashboard>().AsNoTracking().OrderBy(d => d.Id)
            .Select(d => new { d.Id, d.Name, d.OwnerUserId, d.LayoutJson }).ToListAsync(ct);
        var personal = await db.Set<DashboardPersonalVersion>().AsNoTracking()
            .OrderBy(p => p.DashboardId).ThenBy(p => p.UserId)
            .Select(p => new { p.DashboardId, p.UserId, p.LayoutJson }).ToListAsync(ct);
        var names = dashboards.ToDictionary(d => d.Id, d => d.Name);
        return
        [
            .. dashboards.Select(d => new DashboardLayoutSource(d.Id, d.Name, d.OwnerUserId, false, d.LayoutJson)),
            .. personal.Select(p => new DashboardLayoutSource(p.DashboardId, names.GetValueOrDefault(p.DashboardId, $"#{p.DashboardId}"), p.UserId, true, p.LayoutJson)),
        ];
    }

    public Task<int> CountRecordsAsync(long moduleId, bool deleted, CancellationToken ct) =>
        db.Set<Record>().CountAsync(r => r.ModuleId == moduleId && r.DeleteFlag == deleted, ct);

    public Task<int> CountIndexRowsByModuleAsync(long moduleId, CancellationToken ct) =>
        db.Set<RecordIndex>().CountAsync(i => db.Set<Record>().Any(r => r.Id == i.RecordId && r.ModuleId == moduleId), ct);

    public Task<int> CountHistoryRowsByModuleAsync(long moduleId, CancellationToken ct) =>
        db.Set<RecordHistory>().CountAsync(h => db.Set<Record>().Any(r => r.Id == h.RecordId && r.ModuleId == moduleId), ct);

    public Task<int> CountIndexRowsByFieldsAsync(IReadOnlyCollection<long> fieldIds, CancellationToken ct)
    {
        var ids = fieldIds.ToList();
        return db.Set<RecordIndex>().CountAsync(i => ids.Contains(i.FieldId), ct);
    }

    public Task<int> CountHistoryRowsByFieldsAsync(IReadOnlyCollection<long> fieldIds, CancellationToken ct)
    {
        var ids = fieldIds.ToList();
        return db.Set<RecordHistory>().CountAsync(h => h.FieldId != null && ids.Contains(h.FieldId.Value), ct);
    }

    public async Task<IReadOnlyDictionary<string, int>> CountLiveRecordsWithValueAsync(
        long moduleId, IReadOnlyCollection<string> fieldKeys, CancellationToken ct)
    {
        var counts = new Dictionary<string, int>();
        if (fieldKeys.Count == 0) return counts;
        // 只讀 ValuesJson 串流；有值才算，空值定義見 IsEmpty
        var rows = db.Set<Record>().AsNoTracking().Where(r => r.ModuleId == moduleId && !r.DeleteFlag)
            .Select(r => r.ValuesJson).AsAsyncEnumerable();
        await foreach (var json in rows.WithCancellation(ct))
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var key in fieldKeys)
                if (doc.RootElement.TryGetProperty(key, out var v) && !IsEmpty(v))
                    counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        return counts;
    }

    /// <summary>空值：null、空白字串、空陣列；數字 0 與 false 是有值。</summary>
    internal static bool IsEmpty(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()),
        JsonValueKind.Array => v.GetArrayLength() == 0,
        _ => false
    };

    public async Task<List<Record>> ListRecordsWithKeysAsync(long moduleId, IReadOnlyCollection<string> fieldKeys, CancellationToken ct)
    {
        var found = new Dictionary<long, Record>();
        foreach (var key in fieldKeys)
        {
            var pattern = $"%\"{key}\"%";
            var rows = await db.Set<Record>().Where(r => r.ModuleId == moduleId && EF.Functions.Like(r.ValuesJson, pattern)).ToListAsync(ct);
            foreach (var r in rows) found[r.Id] = r;
        }
        return found.Values.OrderBy(r => r.Id).ToList();
    }

    public async Task PurgeModuleAsync(Module module, CancellationToken ct)
    {
        var recordIds = db.Set<Record>().Where(r => r.ModuleId == module.Id).Select(r => r.Id);
        await db.Set<RecordHistory>().Where(h => recordIds.Contains(h.RecordId)).ExecuteDeleteAsync(ct);
        await db.Set<RecordIndex>().Where(i => recordIds.Contains(i.RecordId)).ExecuteDeleteAsync(ct);
        await db.Set<Record>().Where(r => r.ModuleId == module.Id).ExecuteDeleteAsync(ct);
        await db.Set<NumberSequence>().Where(n => n.ModuleId == module.Id).ExecuteDeleteAsync(ct);
        await db.Set<ModuleDataScope>().Where(s => s.ModuleId == module.Id).ExecuteDeleteAsync(ct);
        await PurgeFieldsAsync(module.Blocks.SelectMany(b => b.Fields).ToList(), ct);
        await db.Set<GroupProgramPrivilege>().Where(p => p.ProgramCode == module.ProgramCode).ExecuteDeleteAsync(ct);
        await db.Set<UserProgramPrivilege>().Where(p => p.ProgramCode == module.ProgramCode).ExecuteDeleteAsync(ct);
        await db.Set<DynamicProgram>().Where(p => p.ProgramCode == module.ProgramCode).ExecuteDeleteAsync(ct);
    }

    public async Task PurgeFieldsAsync(IReadOnlyCollection<Field> fields, CancellationToken ct)
    {
        var fieldIds = fields.Select(f => f.Id).ToList();
        var optionIds = fields.SelectMany(f => f.Options).Select(o => o.Id).ToList();
        await db.Set<RecordHistory>().Where(h => h.FieldId != null && fieldIds.Contains(h.FieldId.Value)).ExecuteDeleteAsync(ct);
        await db.Set<RecordIndex>().Where(i => fieldIds.Contains(i.FieldId)).ExecuteDeleteAsync(ct);
        await db.Set<FieldPermission>().Where(p => fieldIds.Contains(p.FieldId)).ExecuteDeleteAsync(ct);
        await db.Set<FieldOptionScope>().Where(s => optionIds.Contains(s.OptionId)).ExecuteDeleteAsync(ct);
        await db.Set<NumberSequence>().Where(n => fieldIds.Contains(n.FieldId)).ExecuteDeleteAsync(ct);
    }
}
