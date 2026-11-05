using DynamicModules.Application.Dynamic.References;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Mock;
using DynamicModules.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.References;

public class MockCustomerProvider(DynamicDbContext db) : IReferenceProvider
{
    public string EntityKey => "sales.customer";

    public I18nText Name { get; } = new(
    [
        KeyValuePair.Create("zh-TW", "客戶"), KeyValuePair.Create("en-US", "Customer"),
        KeyValuePair.Create("th", "ลูกค้า"), KeyValuePair.Create("vi", "Khách hàng")
    ]);

    public async Task<LightPage> LightAsync(string? keyword, int page, int size, CancellationToken ct)
    {
        var query = db.Set<MockCustomer>().Where(m => !m.DeleteFlag);
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var k = keyword.Trim();
            query = query.Where(m => m.CustomerNo.Contains(k) || m.Name.Contains(k));
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(m => m.CustomerNo).Skip((page - 1) * size).Take(size)
            .Select(m => new { m.CustomerNo, m.Name }).ToListAsync(ct);
        return new LightPage(rows.Select(m => new LightItem(m.CustomerNo, $"{m.CustomerNo} {m.Name}")).ToList(), total);
    }

    public async Task<IReadOnlyDictionary<string, string>> TitlesAsync(IReadOnlyCollection<string> keys, CancellationToken ct) =>
        (await db.Set<MockCustomer>().Where(m => !m.DeleteFlag && keys.Contains(m.CustomerNo))
            .Select(m => new { m.CustomerNo, m.Name }).ToListAsync(ct))
        .ToDictionary(m => m.CustomerNo, m => $"{m.CustomerNo} {m.Name}");
}
