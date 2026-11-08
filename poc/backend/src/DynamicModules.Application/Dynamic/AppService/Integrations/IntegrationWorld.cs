using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.AppService.Integrations;

/// <summary>
/// 一個整合的符號空間：擁有者的代號用原樣，夥伴的加 &lt;應用程式 id&gt;/ 前綴，全部解析成實際的鍵與 Id。
/// 來源是開啟時存下的對照表（Dynamic_AppField／AppOption／AppBlock），不是任何目錄。
/// </summary>
internal sealed class IntegrationWorld
{
    public Dictionary<string, string> ModuleKeys { get; } = new();
    public Dictionary<string, Module> OwnerModules { get; } = new();
    public Dictionary<string, long> FieldIds { get; } = new();
    public Dictionary<string, FieldType> FieldTypes { get; } = new();
    public Dictionary<string, long> OptionIds { get; } = new();
    public Dictionary<string, long> OwnerBlocks { get; } = new();
    public Dictionary<long, Field> Fields { get; } = new();

    public AppReportCodec.Lookups Lookups(HashSet<string> used) => new(
        code => ModuleKeys.GetValueOrDefault(code),
        name => FieldIds.TryGetValue(name, out var id) ? Use(used, name, id) : null,
        name => OptionIds.TryGetValue(name, out var id) ? id : null,
        name => FieldTypes.TryGetValue(name, out var type) ? type : null);

    private static long Use(HashSet<string> used, string name, long id)
    {
        used.Add(name);
        return id;
    }

    /// <summary>用到的欄位若不是啟用的（夥伴的設計者停用了它），回第一個名稱；整合不能靜默地用停用的欄位。</summary>
    public string? InactiveSymbol(IEnumerable<string> names) =>
        names.FirstOrDefault(n => FieldIds.TryGetValue(n, out var id) && Fields.TryGetValue(id, out var f) && f.Status != FieldStatus.Active);

    public static async Task<(IntegrationWorld? World, string? Problem)> LoadAsync(
        App owner, Data.AppIntegrationDecl decl, List<App> appList, IAppRepository apps, IModuleRepository modules, CancellationToken ct)
    {
        var world = new IntegrationWorld();
        async Task<string?> Add(App app, string prefix, IReadOnlyCollection<string>? only)
        {
            var bindings = await apps.GetBindingsAsync(app.Id, ct);
            if (bindings.Fields.Count == 0)
                return $"應用程式 {app.Key} 是在整合功能之前開啟的，缺少欄位對照，請重建";
            foreach (var member in app.Modules.Where(m => only is null || only.Contains(m.Code)))
            {
                var module = await modules.GetByIdAsync(member.ModuleId, includeChildren: true, ct);
                if (module is null) return $"模組 {prefix}{member.Code} 不存在";
                world.ModuleKeys[prefix + member.Code] = module.Key;
                if (prefix == "") world.OwnerModules[member.Code] = module;
                foreach (var field in module.Blocks.SelectMany(b => b.Fields)) world.Fields[field.Id] = field;
            }
            bool Mine(string name) => only is null || only.Contains(name[..name.IndexOf('.')]);
            foreach (var (name, id) in bindings.Fields.Where(x => Mine(x.Key)))
            {
                world.FieldIds[prefix + name] = id;
                if (world.Fields.TryGetValue(id, out var f)) world.FieldTypes[prefix + name] = f.Type;
            }
            foreach (var (name, id) in bindings.Options.Where(x => Mine(x.Key))) world.OptionIds[prefix + name] = id;
            if (prefix == "") foreach (var (name, id) in bindings.Blocks) world.OwnerBlocks[name] = id;
            return null;
        }

        if (await Add(owner, "", null) is { } ownerProblem) return (null, ownerProblem);
        foreach (var w in decl.With)
        {
            var partner = appList.FirstOrDefault(a => a.Key == w.Id);
            if (partner is null) return (null, $"夥伴 {w.Id} 不存在");
            if (await Add(partner, $"{w.Id}/", w.Modules) is { } partnerProblem) return (null, partnerProblem);
        }
        return (world, null);
    }
}
