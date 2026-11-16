using DynamicModules.Application.Dynamic.AppService.Data;

namespace DynamicModules.Application.Dynamic.AppService;

/// <summary>
/// 內建應用程式套件（singleton）：讀 apps/*.app.json。目錄不存在或沒有檔案是合法的（空系統）；
/// 任何檔案解析、驗證失敗、id 重複、requires（含 requires.masters）循環或主檔對帳不符，丟 InvalidOperationException 拒絕啟動。
/// </summary>
public sealed class AppCatalog(IReadOnlyList<AppPackageData> builtIns)
{
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "apps");

    public IReadOnlyList<AppPackageData> BuiltIns { get; } = builtIns;

    public static AppCatalog Load(string directory)
    {
        if (!Directory.Exists(directory)) return new AppCatalog([]);

        var parsedFiles = new List<(string File, AppPackageData Package)>();
        var errors = new List<string>();
        foreach (var path in Directory.GetFiles(directory, "*.app.json").Order(StringComparer.Ordinal))
        {
            var file = Path.GetFileName(path);
            var parsed = AppJson.Parse(File.ReadAllBytes(path));
            if (!parsed.Ok) { errors.AddRange(parsed.Errors.Select(e => $"{file}: {e}")); continue; }
            if (parsed.V2 is not { } package) { errors.Add($"{file}: 內建套件必須是 formatVersion 2"); continue; }
            parsedFiles.Add((file, package));
        }

        // 目錄層對帳（共用主檔 §4.6）：內建套件引用的主檔欄位與選項都要在內建主檔裡，貢獻代號不得與基本代號相同
        var masters = parsedFiles.Where(x => x.Package.Manifest.IsMaster)
            .GroupBy(x => x.Package.Manifest.Id).ToDictionary(g => g.Key, g => g.First().Package);
        var packages = new List<AppPackageData>();
        var ids = new Dictionary<string, string>();
        foreach (var (file, package) in parsedFiles)
        {
            var validation = AppPackageValidator.Validate(package, masters);
            errors.AddRange(validation.Errors.Select(e => $"{file}: {e}"));
            foreach (var missing in package.Manifest.Requires.Masters.Where(r => !masters.ContainsKey(r.Id)))
                errors.Add($"{file}: requires.masters 的主檔 {missing.Id} 不在內建套件裡");
            if (!ids.TryAdd(package.Manifest.Id, file)) errors.Add($"{file}: 應用程式代號 {package.Manifest.Id} 與 {ids[package.Manifest.Id]} 重複");
            else if (validation.Errors.Count == 0) packages.Add(package);
        }
        if (AppDependencyGraph.FindCycle(packages.Select(p => (p.Manifest.Id, AppDependencyGraph.RequiresOf(p.Manifest.Requires)))) is { } cycle)
            errors.Add($"內建套件的 requires 有循環：{string.Join(" → ", cycle)}");
        errors.AddRange(AppCatalogChecks.Run(packages));
        if (errors.Count > 0)
            throw new InvalidOperationException("內建應用程式載入失敗：" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        return new AppCatalog(packages);
    }
}
