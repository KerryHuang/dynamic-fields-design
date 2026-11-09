using System.Text.Json;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.AppService.SharedMasters;

/// <summary>
/// 已開啟主檔的符號：主檔名下的對照列（名稱不含 /，就是基本欄位、選項、區塊）加上 &lt;主檔 id&gt;/ 前綴，
/// 開啟使用者套件時預先放進對照（設計 §4.4、§10）。別的應用程式的貢獻在它們自己名下，看不到。
/// </summary>
public sealed class MasterSymbols(IAppRepository apps, IModuleRepository modules)
{
    public async Task<InstantiationSeeds> SeedsAsync(IEnumerable<App> masters, CancellationToken ct)
    {
        var fields = new Dictionary<string, long>();
        var fieldKeys = new Dictionary<string, string>();
        var options = new Dictionary<string, long>();
        var moduleMap = new Dictionary<string, (long Id, string Key)>();
        foreach (var master in masters)
        {
            var bindings = await apps.GetBindingsAsync(master.Id, ct);
            var keyOf = new Dictionary<long, string>();
            foreach (var member in master.Modules)
                if (await modules.GetByIdAsync(member.ModuleId, includeChildren: true, ct) is { } module)
                {
                    moduleMap[$"{master.Key}/{member.Code}"] = (module.Id, module.Key);
                    foreach (var f in module.Blocks.SelectMany(b => b.Fields)) keyOf[f.Id] = f.Key;
                }
            foreach (var (name, id) in bindings.Fields.Where(x => !x.Key.Contains('/')))
            {
                fields[$"{master.Key}/{name}"] = id;
                if (keyOf.TryGetValue(id, out var key)) fieldKeys[$"{master.Key}/{name}"] = key;
            }
            foreach (var (name, id) in bindings.Options.Where(x => !x.Key.Contains('/')))
                options[$"{master.Key}/{name}"] = id;
        }
        return new InstantiationSeeds(fields, fieldKeys, options, moduleMap);
    }

    /// <summary>
    /// 資料庫裡主檔現在的樣子，寫成套件定義（只有基本欄位與選項）：給驗證器對帳，客戶刪掉或改了型別的基本欄位會在這裡現形（§4.6 的 unbound）。
    /// 模組層的旗標（extensible）沿用套件原文，找不到原文時視為可擴充。
    /// </summary>
    public async Task<AppPackageData> DefinitionAsync(App master, AppPackageData? package, CancellationToken ct)
    {
        var bindings = await apps.GetBindingsAsync(master.Id, ct);
        var optionCode = bindings.Options.Where(x => !x.Key.Contains('/')).ToDictionary(x => x.Value, x => x.Key[(x.Key.IndexOf('.') + 1)..]);
        var result = new List<TemplateModule>();
        foreach (var member in master.Modules)
        {
            var module = await modules.GetByIdAsync(member.ModuleId, includeChildren: true, ct);
            if (module is null) continue;
            var byId = module.Blocks.SelectMany(b => b.Fields).ToDictionary(f => f.Id);
            var fields = bindings.Fields
                .Where(x => !x.Key.Contains('/') && x.Key.StartsWith(member.Code + ".", StringComparison.Ordinal) && byId.ContainsKey(x.Value))
                .Select(x =>
                {
                    var f = byId[x.Value];
                    var opts = f.Options.Where(o => optionCode.ContainsKey(o.Id))
                        .Select(o => new TemplateOption(optionCode[o.Id], o.LabelI18n, o.Color)).ToList();
                    return new TemplateField(x.Key[(member.Code.Length + 1)..], f.Type, f.LabelI18n, EmptyConfig, opts);
                }).ToList();
            var declared = package?.Modules.FirstOrDefault(m => m.Code == member.Code);
            result.Add(new TemplateModule(member.Code, module.Key, module.NameI18n, module.Icon, module.Section, module.Kind, null,
                declared?.TitleField, [new TemplateBlock("main", new I18nText(), false, fields)], [], [], [],
                Extensible: declared?.Extensible ?? true));
        }
        var manifest = new AppManifest(master.Key, master.PackageVersion, master.NameI18n, null, master.Icon, null,
            new AppManifestRequires([], [], master.Requires.Masters.Select(r => new AppRequirementData(r.Id, r.Modules)).ToList()),
            AppManifest.MasterKindName);
        return new AppPackageData(AppPackageData.FormatName, AppPackageData.CurrentFormatVersion, manifest, [], result,
            null, null, null, null, null, null, null, null);
    }

    private static readonly JsonElement EmptyConfig = JsonDocument.Parse("{}").RootElement.Clone();
}
