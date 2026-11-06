using DynamicModules.Application.Dynamic.AppService.Data;

namespace DynamicModules.Application.Dynamic.AppService;

/// <summary>
/// 內建應用程式套件（singleton）：讀 apps/*.app.json。目錄不存在或沒有檔案是合法的（空系統）；
/// 任何檔案解析、驗證失敗、id 重複或 requires 循環，丟 InvalidOperationException 拒絕啟動。
/// </summary>
public sealed class AppCatalog(IReadOnlyList<AppPackageData> builtIns)
{
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "apps");

    public IReadOnlyList<AppPackageData> BuiltIns { get; } = builtIns;

    public static AppCatalog Load(string directory)
    {
        if (!Directory.Exists(directory)) return new AppCatalog([]);

        var packages = new List<AppPackageData>();
        var ids = new Dictionary<string, string>();
        var errors = new List<string>();
        foreach (var path in Directory.GetFiles(directory, "*.app.json").Order(StringComparer.Ordinal))
        {
            var file = Path.GetFileName(path);
            var parsed = AppJson.Parse(File.ReadAllBytes(path));
            if (!parsed.Ok) { errors.AddRange(parsed.Errors.Select(e => $"{file}: {e}")); continue; }
            if (parsed.V2 is not { } package) { errors.Add($"{file}: 內建套件必須是 formatVersion 2"); continue; }
            var validation = AppPackageValidator.Validate(package);
            errors.AddRange(validation.Errors.Select(e => $"{file}: {e}"));
            if (!ids.TryAdd(package.Manifest.Id, file)) errors.Add($"{file}: 應用程式代號 {package.Manifest.Id} 與 {ids[package.Manifest.Id]} 重複");
            else if (validation.Errors.Count == 0) packages.Add(package);
        }
        if (AppDependencyGraph.FindCycle(packages.Select(p => (p.Manifest.Id, (IReadOnlyList<string>)p.Manifest.Requires.Apps.Select(a => a.Id).ToList()))) is { } cycle)
            errors.Add($"內建套件的 requires 有循環：{string.Join(" → ", cycle)}");
        if (errors.Count > 0)
            throw new InvalidOperationException("內建應用程式載入失敗：" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        return new AppCatalog(packages);
    }
}
