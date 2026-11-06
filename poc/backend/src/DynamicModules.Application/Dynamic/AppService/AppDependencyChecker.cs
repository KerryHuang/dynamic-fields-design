using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.AppService;

/// <summary>跨應用程式的相依：開啟與重新開啟時檢查「我需要的在不在」，關閉與刪除時查「誰需要我」。</summary>
public sealed class AppDependencyChecker(IAppRepository apps, IModuleRepository modules, ReferenceProviderRegistry providers)
{
    public sealed record RequirementResult(IReadOnlyList<MissingDependency> Missing, IReadOnlyDictionary<string, string> ExternalModuleKeys);

    /// <summary>
    /// requires.apps 的每一項都要：應用程式已開啟、模組還在、模組 Active。通過時 ExternalModuleKeys 是
    /// 「應用程式 id/模組代號」→ 實際 Module.Key。requires.entities 要有對應的 IReferenceProvider。
    /// 沒有「先建、之後再補」的懸空參照。
    /// </summary>
    public async Task<RequirementResult> CheckRequirementsAsync(AppRequires requires, CancellationToken ct)
    {
        var missing = new List<MissingDependency>();
        var keys = new Dictionary<string, string>();
        foreach (var req in requires.Apps)
        {
            var app = await apps.GetByKeyAsync(req.Id, ct);
            if (app is null) { missing.Add(new("app", req.Id, null, null, "notInstalled")); continue; }
            if (app.Status == AppStatus.Closed) { missing.Add(new("app", req.Id, null, null, "closed")); continue; }
            foreach (var code in req.Modules)
            {
                var member = app.Modules.FirstOrDefault(m => m.Code == code);
                if (member is null) { missing.Add(new("app", req.Id, code, null, "moduleMissing")); continue; }
                var module = await modules.GetByIdAsync(member.ModuleId, includeChildren: false, ct);
                if (module is null) missing.Add(new("app", req.Id, code, null, "moduleMissing"));
                else if (module.Status != ModuleStatus.Active) missing.Add(new("app", req.Id, code, null, "moduleNotActive"));
                else keys[$"{req.Id}/{code}"] = module.Key;
            }
        }
        foreach (var entity in requires.Entities)
            if (providers.Get(entity) is null) missing.Add(new("entity", null, null, entity, "entityMissing"));
        return new RequirementResult(missing, keys);
    }

    public sealed record DependentsResult(IReadOnlyList<App> Apps, IReadOnlyList<string> UnaffiliatedReferrers);

    /// <summary>
    /// 誰依賴 <paramref name="target"/>：開啟中的其他應用程式，宣告了它（requires.apps），或有開啟狀態欄位的參照指向它的成員模組。
    /// 不屬於任何應用程式的模組若有欄位參照它，列在 UnaffiliatedReferrers（只警告，不擋）。
    /// </summary>
    public async Task<DependentsResult> DependentsOfAsync(App target, CancellationToken ct)
    {
        var all = await apps.ListAsync(ct);
        var appOfModule = all.SelectMany(a => a.Modules.Select(m => (m.ModuleId, App: a))).ToDictionary(x => x.ModuleId, x => x.App);
        var dependents = new Dictionary<string, App>();
        foreach (var other in all.Where(a => a.Key != target.Key && a.Status == AppStatus.Open))
            if (other.Requires.Apps.Any(r => r.Id == target.Key)) dependents[other.Key] = other;

        var unaffiliated = new HashSet<string>();
        foreach (var member in target.Modules)
        {
            var module = await modules.GetByIdAsync(member.ModuleId, includeChildren: false, ct);
            if (module is null) continue;
            foreach (var (source, _) in await modules.ListIncomingReferenceFieldsAsync(module.Key, ct))
            {
                if (!appOfModule.TryGetValue(source.Id, out var owner)) unaffiliated.Add(source.Key);
                else if (owner.Key != target.Key && owner.Status == AppStatus.Open) dependents[owner.Key] = owner;
            }
        }
        return new DependentsResult(dependents.Values.OrderBy(a => a.Key, StringComparer.Ordinal).ToList(), unaffiliated.Order().ToList());
    }

    /// <summary>連帶關閉用：<paramref name="target"/> 加上遞移的依賴者，依賴者在前。</summary>
    public async Task<IReadOnlyList<App>> CascadeOrderAsync(App target, CancellationToken ct)
    {
        var order = new List<App>();
        var visited = new HashSet<string> { target.Key };
        async Task VisitAsync(App app)
        {
            foreach (var dependent in (await DependentsOfAsync(app, ct)).Apps.Where(d => visited.Add(d.Key)))
                await VisitAsync(dependent);
            order.Add(app);
        }
        await VisitAsync(target);
        return order;
    }
}
