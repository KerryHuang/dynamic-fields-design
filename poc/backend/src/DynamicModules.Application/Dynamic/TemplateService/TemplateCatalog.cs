using DynamicModules.Application.Dynamic.TemplateService.Data;

namespace DynamicModules.Application.Dynamic.TemplateService;

/// <summary>內建範本（singleton）。站台啟動時由 Load 讀入並驗證，有錯就拒絕啟動；測試可直接用套件清單建構。</summary>
public sealed class TemplateCatalog(IReadOnlyList<TemplatePackage> builtIns)
{
    /// <summary>站台輸出目錄下的 templates/（由 WebAPI csproj 從 poc/backend/templates 複製）。</summary>
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "templates");

    public IReadOnlyList<TemplatePackage> BuiltIns { get; } = builtIns;

    /// <summary>讀目錄下所有 *.json；任何檔案解析或驗證失敗、或範本 key 重複，丟 InvalidOperationException（含檔名與全部錯誤）。</summary>
    public static TemplateCatalog Load(string directory)
    {
        if (!Directory.Exists(directory))
            throw new InvalidOperationException($"內建範本載入失敗：找不到目錄 {directory}");

        var packages = new List<TemplatePackage>();
        var keys = new Dictionary<string, string>();
        var errors = new List<string>();
        foreach (var path in Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            var file = Path.GetFileName(path);
            var (package, parseError) = TemplateJson.Parse(File.ReadAllText(path));
            if (package is null)
            {
                errors.Add($"{file}: {parseError}");
                continue;
            }
            var fileErrors = TemplateValidator.Validate(package);
            errors.AddRange(fileErrors.Select(e => $"{file}: {e}"));
            if (package.Key is not null && !keys.TryAdd(package.Key, file))
                errors.Add($"{file}: 範本代號 {package.Key} 與 {keys[package.Key]} 重複");
            else if (fileErrors.Count == 0) packages.Add(package);
        }

        if (errors.Count > 0)
            throw new InvalidOperationException("內建範本載入失敗：" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        return new TemplateCatalog(packages);
    }
}
