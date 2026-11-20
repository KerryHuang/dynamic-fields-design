using System.Text.Json;

namespace DynamicModules.DomainTests.Localization;

/// <summary>從測試輸出目錄往上找 repo 根目錄的 shared/ 檔。</summary>
internal static class SharedFile
{
    public static JsonElement Load(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "shared", name)))
            dir = dir.Parent;
        if (dir is null) throw new FileNotFoundException($"找不到 shared/{name}");
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "shared", name))).RootElement.Clone();
    }
}
