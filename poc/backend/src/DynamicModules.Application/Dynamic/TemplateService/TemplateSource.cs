using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.TemplateService;

/// <summary>可用的範本：內建（TemplateCatalog）與匯入的。</summary>
public interface ITemplateSource
{
    Task<TemplatePackage?> FindAsync(string key, CancellationToken ct);

    /// <summary>Source 是 builtIn 或 imported；內建在前、各自依 key 排序。</summary>
    Task<IReadOnlyList<(TemplatePackage Package, string Source)>> ListAsync(CancellationToken ct);
}

public sealed class TemplateSource(TemplateCatalog catalog, IModuleTemplateRepository imported) : ITemplateSource
{
    public const string BuiltIn = "builtIn";
    public const string Imported = "imported";

    public async Task<TemplatePackage?> FindAsync(string key, CancellationToken ct)
    {
        if (catalog.BuiltIns.FirstOrDefault(p => p.Key == key) is { } builtIn) return builtIn;
        var row = await imported.GetByKeyAsync(key, ct);
        return row is null ? null : TemplateJson.Parse(row.Json).Package;
    }

    public async Task<IReadOnlyList<(TemplatePackage Package, string Source)>> ListAsync(CancellationToken ct)
    {
        var list = catalog.BuiltIns.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p, BuiltIn)).ToList();
        foreach (var row in await imported.ListAsync(ct))
            if (TemplateJson.Parse(row.Json).Package is { } p) list.Add((p, Imported));
        return list;
    }
}
