using DynamicModules.Application.Dynamic.References;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Mock;
using DynamicModules.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.References;

public class MockMoldProvider(DynamicDbContext db) : IReferenceProvider
{
    public string EntityKey => "design.mold";

    public I18nText Name { get; } = new(
    [
        KeyValuePair.Create("zh-TW", "模具"), KeyValuePair.Create("en-US", "Mold"),
        KeyValuePair.Create("th", "แม่พิมพ์"), KeyValuePair.Create("vi", "Khuôn")
    ]);

    public async Task<LightPage> LightAsync(string? keyword, int page, int size, CancellationToken ct)
    {
        var query = db.Set<MockMold>().Where(m => !m.DeleteFlag);
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var k = keyword.Trim();
            query = query.Where(m => m.MoldNo.Contains(k) || m.Name.Contains(k));
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(m => m.MoldNo).Skip((page - 1) * size).Take(size)
            .Select(m => new { m.MoldNo, m.Name }).ToListAsync(ct);
        return new LightPage(rows.Select(m => new LightItem(m.MoldNo, $"{m.MoldNo} {m.Name}")).ToList(), total);
    }

    public async Task<IReadOnlyDictionary<string, string>> TitlesAsync(IReadOnlyCollection<string> keys, CancellationToken ct) =>
        (await db.Set<MockMold>().Where(m => !m.DeleteFlag && keys.Contains(m.MoldNo))
            .Select(m => new { m.MoldNo, m.Name }).ToListAsync(ct))
        .ToDictionary(m => m.MoldNo, m => $"{m.MoldNo} {m.Name}");
}
