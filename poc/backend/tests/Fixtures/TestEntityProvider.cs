using DynamicModules.Application.Dynamic.References;
using DynamicModules.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.TestSupport;

/// <summary>
/// 只在測試專案註冊的既有實體提供者（`test.entity`）：產品程式不留模擬實體，測試要「外部系統既有實體」時用它。
/// 資料放在記憶體，每個 DI 容器一份；測試可以直接新增或標記刪除。
/// </summary>
public sealed class TestEntityProvider : IReferenceProvider
{
    public const string Key = "test.entity";

    /// <summary>每個容器一開始就有的 12 筆。</summary>
    public static readonly (string Key, string Name)[] Seed =
    [
        ("E-0001", "甲號"), ("E-0002", "乙號"), ("E-0003", "丙號"), ("E-0004", "丁號"), ("E-0005", "戊號"), ("E-0006", "己號"),
        ("E-0007", "庚號"), ("E-0008", "辛號"), ("E-0009", "壬號"), ("E-0010", "癸號"), ("E-0011", "子號"), ("E-0012", "丑號")
    ];

    private readonly object _gate = new();
    private readonly List<(string Key, string Name)> _rows = [.. Seed];
    private readonly HashSet<string> _deleted = [];

    public string EntityKey => Key;

    public I18nText Name { get; } = new(
    [
        KeyValuePair.Create("zh-TW", "測試實體"), KeyValuePair.Create("en-US", "Test entity"),
        KeyValuePair.Create("th", "เอนทิตีทดสอบ"), KeyValuePair.Create("vi", "Thực thể thử nghiệm")
    ]);

    public void Add(string key, string name)
    {
        lock (_gate) _rows.Add((key, name));
    }

    /// <summary>標記刪除：之後 Light 與 Titles 都看不到它。</summary>
    public void Delete(string key)
    {
        lock (_gate) _deleted.Add(key);
    }

    private List<(string Key, string Name)> Alive()
    {
        lock (_gate) return _rows.Where(r => !_deleted.Contains(r.Key)).ToList();
    }

    public Task<LightPage> LightAsync(string? keyword, int page, int size, CancellationToken ct)
    {
        var rows = Alive();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var k = keyword.Trim();
            rows = rows.Where(r => r.Key.Contains(k, StringComparison.Ordinal) || r.Name.Contains(k, StringComparison.Ordinal)).ToList();
        }
        var items = rows.OrderBy(r => r.Key, StringComparer.Ordinal).Skip((page - 1) * size).Take(size)
            .Select(r => new LightItem(r.Key, $"{r.Key} {r.Name}")).ToList();
        return Task.FromResult(new LightPage(items, rows.Count));
    }

    public Task<IReadOnlyDictionary<string, string>> TitlesAsync(IReadOnlyCollection<string> keys, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(
            Alive().Where(r => keys.Contains(r.Key)).ToDictionary(r => r.Key, r => $"{r.Key} {r.Name}"));
}

public static class TestEntityServices
{
    /// <summary>註冊 <see cref="TestEntityProvider"/>（單例，測試可取出來改資料）。</summary>
    public static IServiceCollection AddTestEntities(this IServiceCollection services)
    {
        services.AddSingleton<TestEntityProvider>();
        services.AddSingleton<IReferenceProvider>(sp => sp.GetRequiredService<TestEntityProvider>());
        return services;
    }
}
