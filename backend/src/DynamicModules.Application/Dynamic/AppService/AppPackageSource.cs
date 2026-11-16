using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.AppService;

public record SourcedPackage(AppPackageData Package, string Source);

/// <summary>可用的應用程式套件：內建（AppCatalog）與匯入的。匯入的原文每次讀取才升級成 v2，不回寫。</summary>
public sealed class AppPackageSource(AppCatalog catalog, TemplateCatalog templates, IAppPackageRepository imported)
{
    public const string BuiltIn = "builtIn";
    public const string Imported = "imported";

    /// <summary>
    /// 內建優先；只有共用主檔例外：再匯入主檔會把合併結果存成匯入的那份（共用主檔設計 §9），所以主檔是匯入的優先於內建。
    /// </summary>
    public async Task<SourcedPackage?> FindAsync(string id, CancellationToken ct)
    {
        var builtIn = catalog.BuiltIns.FirstOrDefault(p => p.Manifest.Id == id);
        if (builtIn is { Manifest.IsMaster: false }) return new(builtIn, BuiltIn);
        if (await imported.GetByKeyAsync(id, ct) is { } row && (await ReadAsync(row.Json, ct)).Package is { } package
            && (builtIn is null || package.Manifest.IsMaster))
            return new(package, Imported);
        return builtIn is null ? null : new(builtIn, BuiltIn);
    }

    /// <summary>內建在前、各自依 id 排序；匯入的壞資料略過。內建主檔有合併過的匯入版時以匯入的取代，同一個 id 只列一次。</summary>
    public async Task<IReadOnlyList<SourcedPackage>> ListAsync(CancellationToken ct)
    {
        var list = catalog.BuiltIns.OrderBy(p => p.Manifest.Id, StringComparer.Ordinal).Select(p => new SourcedPackage(p, BuiltIn)).ToList();
        foreach (var row in await imported.ListAsync(ct))
        {
            if ((await ReadAsync(row.Json, ct)).Package is not { } p) continue;
            var index = list.FindIndex(x => x.Package.Manifest.Id == p.Manifest.Id);
            if (index < 0) list.Add(new(p, Imported));
            else if (list[index].Package.Manifest.IsMaster && p.Manifest.IsMaster) list[index] = new(p, Imported);
        }
        return list;
    }

    /// <summary>讀套件原文成 v2；v1 經升級器，「其他已知套件」取內建與匯入的 v2 套件，加上內建範本。</summary>
    public async Task<(AppPackageData? Package, IReadOnlyList<string> Errors)> ReadAsync(string json, CancellationToken ct) =>
        await ReadAsync(AppJson.Parse(json), ct);

    public async Task<(AppPackageData? Package, IReadOnlyList<string> Errors)> ReadAsync(AppParseResult parsed, CancellationToken ct)
    {
        if (!parsed.Ok) return (null, parsed.Errors);
        if (parsed.V2 is { } v2) return (v2, []);
        return AppPackageUpgrader.FromTemplate(parsed.V1!, await KnownAsync(parsed.V1!.Key, ct));
    }

    /// <summary>升級 v1 時用來找跨應用程式引用目標的套件：不含自己。</summary>
    private async Task<IReadOnlyList<KnownPackage>> KnownAsync(string exceptId, CancellationToken ct)
    {
        var known = new List<KnownPackage>();
        foreach (var p in catalog.BuiltIns)
            known.Add(new(p.Manifest.Id, p.Modules.Select(m => (m.Code, m.Key)).ToList()));
        foreach (var t in templates.BuiltIns)
            known.Add(new(t.Key, t.Modules.Select(m => (m.Code, m.Key)).ToList()));
        foreach (var row in await imported.ListAsync(ct))
        {
            var parsed = AppJson.Parse(row.Json);
            if (parsed.V2 is { } v2) known.Add(new(v2.Manifest.Id, v2.Modules.Select(m => (m.Code, m.Key)).ToList()));
            else if (parsed.V1 is { } v1) known.Add(new(v1.Key, v1.Modules.Select(m => (m.Code, m.Key)).ToList()));
        }
        return known.Where(k => k.Id != exceptId).DistinctBy(k => k.Id).ToList();
    }
}
