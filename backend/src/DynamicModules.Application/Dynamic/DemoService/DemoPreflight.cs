using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.DemoService;

/// <summary>產生前的衝突檢查：三個內建應用程式與還沒開的共用主檔的模組代碼、已開的共用主檔是否留有記錄、擴充模組的既有實體、應用程式是否已開啟、示範報表與儀表板名稱、示範人員與實體。</summary>
public sealed class DemoPreflight(AppCatalog catalog, IModuleRepository modules, IDemoPeople people, IReportRepository reports,
    IDashboardRepository dashboards, IAppRepository apps, IRecordRepository records)
{
    /// <summary>每一項衝突一則訊息；空清單表示可以產生。</summary>
    public async Task<IReadOnlyList<string>> ConflictsAsync(CancellationToken ct)
    {
        var conflicts = new List<string>();
        foreach (var m in DemoModules())
        {
            if (await modules.KeyExistsAsync(m.Key, ct)) conflicts.Add($"模組代碼 {m.Key} 已存在");
            if (m.HostEntityKey is { } host && await modules.HostEntityExistsAsync(host, ct))
                conflicts.Add($"既有實體 {host} 已有擴充模組");
        }
        // 共用主檔還沒開時會由示範資料一起開；同代號的模組已存在就會撞（§11.5）
        foreach (var (masterId, m) in MasterModules())
            if (await apps.GetByKeyAsync(masterId, ct) is null && await modules.KeyExistsAsync(m.Key, ct))
                conflicts.Add($"模組代碼 {m.Key} 已存在");
        // 應用程式刪除後主檔會留下記錄（只算有效記錄，軟刪除的不算）；示範資料會再建同名公司與聯絡人，先擋下（要重跑請先清掉主檔記錄）
        foreach (var (masterId, m) in MasterModules())
            if (await apps.GetByKeyAsync(masterId, ct) is not null && await modules.GetByKeyAsync(m.Key, false, ct) is { } existing
                && await records.CountByModuleAsync(existing.Id, ct) > 0)
                conflicts.Add(DemoMessages.MasterHasRecords(masterId, m.Key));
        foreach (var name in DemoReports.Names)
            if (await reports.NameExistsAsync(name, ct)) conflicts.Add($"報表「{name}」已存在");
        foreach (var name in DemoDashboards.Names)
            if (await dashboards.AssignedNameExistsAsync(name, ct)) conflicts.Add($"儀表板「{name}」已存在");
        foreach (var id in DemoCatalog.AppIds)
            if (await apps.GetByKeyAsync(id, ct) is not null) conflicts.Add($"應用程式 {id} 已開啟");
        conflicts.AddRange(await people.ConflictsAsync(ct));
        return conflicts;
    }

    /// <summary>三個應用程式都已開啟（視為已產生過）。</summary>
    public async Task<bool> AllPresentAsync(CancellationToken ct)
    {
        foreach (var id in DemoCatalog.AppIds)
            if (await apps.GetByKeyAsync(id, ct) is null) return false;
        return true;
    }

    private IEnumerable<TemplateModule> DemoModules() => DemoCatalog.AppIds.SelectMany(id => BuiltIn(id).Modules);

    /// <summary>示範應用程式 requires.masters 的遞移閉包裡，每個主檔的模組。</summary>
    private IEnumerable<(string MasterId, TemplateModule Module)> MasterModules()
    {
        var seen = new HashSet<string>();
        var queue = new Queue<string>(DemoCatalog.AppIds.SelectMany(id => BuiltIn(id).Manifest.Requires.Masters.Select(r => r.Id)));
        while (queue.TryDequeue(out var id))
        {
            if (!seen.Add(id)) continue;
            var master = BuiltIn(id);
            foreach (var r in master.Manifest.Requires.Masters) queue.Enqueue(r.Id);
            foreach (var m in master.Modules) yield return (id, m);
        }
    }

    private AppPackageData BuiltIn(string id) =>
        catalog.BuiltIns.FirstOrDefault(p => p.Manifest.Id == id) ?? throw new InvalidOperationException($"內建應用程式缺少 {id}");
}
