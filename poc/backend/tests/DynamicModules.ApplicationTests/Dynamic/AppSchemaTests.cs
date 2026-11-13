using System.Reflection;
using System.Text.Json;
using DynamicModules.Application.Dynamic.AppService.Data;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>
/// apps/app.schema.json 是對外的最上層契約（套件設計 §15）：套件 record 的每個 section 都要列在 schema 裡，
/// formatVersion 的上限跟著 CurrentFormatVersion，防止 schema 再度落後（計畫 J12）。
/// </summary>
public class AppSchemaTests
{
    private static JsonElement Schema()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "apps", "app.schema.json");
            if (File.Exists(path)) return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
        }
        throw new FileNotFoundException("找不到 apps/app.schema.json");
    }

    /// <summary>會寫進檔案的屬性：可設定的公開屬性（IsMaster 這類計算屬性不序列化）。</summary>
    private static IEnumerable<string> Properties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite).Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name));

    private static IEnumerable<string> Keys(JsonElement properties) => properties.EnumerateObject().Select(p => p.Name);

    [Fact]
    public void AppSchema_TopLevelCoversEveryPackageSection()
    {
        var schema = Schema();
        Keys(schema.GetProperty("properties")).Should().Contain(Properties(typeof(AppPackageData)));
        schema.GetProperty("properties").GetProperty("formatVersion").GetProperty("enum").EnumerateArray().Select(v => v.GetInt32())
            .Should().Equal(Enumerable.Range(AppPackageData.MinFormatVersion, AppPackageData.CurrentFormatVersion - AppPackageData.MinFormatVersion + 1));

        // manifest 與 requires 也是 additionalProperties: false，格式 6 的 kind、requires.masters 要在
        var manifest = schema.GetProperty("$defs").GetProperty("manifest").GetProperty("properties");
        Keys(manifest).Should().Contain(Properties(typeof(AppManifest)));
        Keys(manifest.GetProperty("requires").GetProperty("properties")).Should().Contain(Properties(typeof(AppManifestRequires)));
    }
}
