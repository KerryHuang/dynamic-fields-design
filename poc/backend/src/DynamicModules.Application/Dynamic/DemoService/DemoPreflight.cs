using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.DemoService;

/// <summary>產生前的衝突檢查：三個內建應用程式的模組代碼與擴充模組的既有實體、應用程式是否已開啟、示範報表與儀表板名稱、示範人員與實體。</summary>
public sealed class DemoPreflight(AppCatalog catalog, IModuleRepository modules, IDemoPeople people, IReportRepository reports,
    IDashboardRepository dashboards, IAppRepository apps)
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

    private IEnumerable<TemplateModule> DemoModules() =>
        DemoCatalog.AppIds.SelectMany(id =>
            (catalog.BuiltIns.FirstOrDefault(p => p.Manifest.Id == id)
             ?? throw new InvalidOperationException($"內建應用程式缺少 {id}")).Modules);
}
