using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Entities.Mock;
using DynamicModules.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Seed;

/// <summary>示範人員與示範實體（使用者、群組、成員、模具、客戶）；Demo 產生器與測試共用同一份清單。</summary>
public static class DemoPeople
{
    public static readonly (string Id, string Name)[] Users =
    [
        ("sales01", "業務一"), ("sales02", "業務二"),
        ("manager01", "業務主管"), ("floor01", "現場人員"),
        ("floor02", "現場人員二"), ("maint01", "保養人員"), ("qa01", "品保人員"),
        ("planner01", "生管人員"), ("buyer01", "採購人員"), ("keeper01", "倉管人員"), ("ops01", "進銷存主管")
    ];

    public static readonly (string Key, string Name, string[] Members)[] Groups =
    [
        ("mis", "MIS", ["admin"]),
        ("sales", "業務", ["sales01", "sales02"]),
        ("sales-manager", "業務主管", ["manager01"]),
        ("floor", "現場人員", ["floor01", "floor02"]),
        ("maint", "保養", ["maint01"]),
        ("qa", "品保", ["qa01"]),
        ("planner", "生管組", ["planner01"]),
        ("purchasing", "採購組", ["buyer01"]),
        ("warehouse", "倉管組", ["keeper01", "planner01"]),
        ("ops-manager", "進銷存管理組", ["ops01"])
    ];

    public static readonly (string No, string Name)[] Molds =
    [
        ("M-0001", "外殼上蓋模"), ("M-0002", "外殼下蓋模"), ("M-0003", "按鍵模"), ("M-0004", "電池蓋模"), ("M-0005", "支架模"),
        ("M-0006", "射出模"), ("M-0007", "側蓋模"), ("M-0008", "旋鈕模"), ("M-0009", "面板模"),
        ("M-0010", "底座模"), ("M-0011", "導光板模"), ("M-0012", "連接器模")
    ];

    public static readonly (string No, string Name)[] Customers =
    [
        ("C-001", "嵐岳精密"), ("C-002", "恆川機械"), ("C-003", "曜陽電子"),
        ("C-004", "岳峰精機"), ("C-005", "澄川塑膠"), ("C-006", "瑞嶺零件"), ("C-007", "拓明模具"), ("C-008", "禾晟電機")
    ];

    /// <summary>補齊示範資料，已存在的不覆寫；會自行 SaveChanges。</summary>
    public static async Task EnsureAsync(DynamicDbContext db, CancellationToken ct = default)
    {
        var users = await db.Set<AppUser>().Select(u => u.UserId).ToListAsync(ct);
        foreach (var (id, name) in Users.Where(u => !users.Contains(u.Id)))
            db.Set<AppUser>().Add(new AppUser { UserId = id, DisplayName = name, IsDemoAccount = true });

        var molds = await db.Set<MockMold>().Select(m => m.MoldNo).ToListAsync(ct);
        foreach (var (no, name) in Molds.Where(m => !molds.Contains(m.No)))
            db.Set<MockMold>().Add(new MockMold { MoldNo = no, Name = name });
        var customers = await db.Set<MockCustomer>().Select(c => c.CustomerNo).ToListAsync(ct);
        foreach (var (no, name) in Customers.Where(c => !customers.Contains(c.No)))
            db.Set<MockCustomer>().Add(new MockCustomer { CustomerNo = no, Name = name });
        await db.SaveChangesAsync(ct);

        var groups = await db.Set<PermissionGroup>().ToListAsync(ct);
        foreach (var (key, name, _) in Groups.Where(g => groups.All(x => x.Key != g.Key)))
        {
            var group = new PermissionGroup { Key = key, NameI18n = new I18nText([KeyValuePair.Create("zh-TW", name)]) };
            db.Set<PermissionGroup>().Add(group);
            groups.Add(group);
        }
        await db.SaveChangesAsync(ct);

        var mappings = await db.Set<UserGroupMapping>().ToListAsync(ct);
        foreach (var (key, _, members) in Groups)
        {
            var groupId = groups.Single(g => g.Key == key).Id;
            foreach (var userId in members.Where(u => !mappings.Any(m => m.GroupId == groupId && m.UserId == u)))
                db.Set<UserGroupMapping>().Add(new UserGroupMapping { GroupId = groupId, UserId = userId });
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>清單中已存在的項目，每項一則訊息。</summary>
    public static async Task<IReadOnlyList<string>> ConflictsAsync(DynamicDbContext db, CancellationToken ct = default)
    {
        var conflicts = new List<string>();
        var users = await db.Set<AppUser>().Select(u => u.UserId).ToListAsync(ct);
        conflicts.AddRange(Users.Where(u => users.Contains(u.Id)).Select(u => $"使用者 {u.Id} 已存在"));
        var groups = await db.Set<PermissionGroup>().Select(g => g.Key).ToListAsync(ct);
        conflicts.AddRange(Groups.Where(g => groups.Contains(g.Key)).Select(g => $"群組 {g.Key} 已存在"));
        var molds = await db.Set<MockMold>().Select(m => m.MoldNo).ToListAsync(ct);
        conflicts.AddRange(Molds.Where(m => molds.Contains(m.No)).Select(m => $"模具 {m.No} 已存在"));
        var customers = await db.Set<MockCustomer>().Select(c => c.CustomerNo).ToListAsync(ct);
        conflicts.AddRange(Customers.Where(c => customers.Contains(c.No)).Select(c => $"客戶 {c.No} 已存在"));
        return conflicts;
    }
}
