using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Entities.Mock;
using DynamicModules.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Seed;

/// <summary>示範人員與示範實體（使用者、群組、成員、客戶）；Demo 產生器與測試共用同一份清單。</summary>
public static class DemoPeople
{
    public static readonly (string Id, string Name)[] Users =
    [
        ("sales01", "業務一"), ("sales02", "業務二"), ("sales03", "業務三"),
        ("manager01", "業務主管"), ("floor01", "現場人員"),
        ("floor02", "現場人員二"), ("maint01", "保養人員"), ("qa01", "品保人員"),
        ("planner01", "生管人員"), ("buyer01", "採購人員"), ("keeper01", "倉管人員"), ("ops01", "進銷存主管"),
        ("designer01", "設計人員")
    ];

    public static readonly (string Key, string Name, string[] Members)[] Groups =
    [
        ("mis", "MIS", ["admin"]),
        ("sales", "業務", ["sales01", "sales02", "sales03"]),
        ("sales-manager", "業務主管", ["manager01"]),
        ("floor", "現場人員", ["floor01", "floor02"]),
        ("maint", "保養", ["maint01"]),
        ("qa", "品保", ["qa01"]),
        ("planner", "生管組", ["planner01"]),
        ("purchasing", "採購組", ["buyer01"]),
        ("warehouse", "倉管組", ["keeper01"]),
        ("ops-manager", "進銷存管理組", ["ops01"]),
        // 只讀進銷存的品項與倉庫：MES 出貨單要選庫存品項與出庫倉（整合），出貨的生管與業務需要這份讀取權限
        ("inv-viewer", "進銷存查詢組", ["planner01", "sales01", "sales02"]),
        // 產品設計人員（Demo 開 CRM 時對應其 designer 群組）
        ("design", "產品設計", ["designer01"])
    ];

    public static readonly (string No, string Name)[] Customers =
    [
        ("C-001", "嵐岳精密"), ("C-002", "恆川機械"), ("C-003", "曜陽電子"),
        ("C-004", "岳峰精機"), ("C-005", "澄川塑膠"), ("C-006", "瑞嶺零件"), ("C-007", "拓明模具"), ("C-008", "禾晟電機")
    ];

    /// <summary>
    /// 示範組織（簽核計畫 a1 Task 5、主控裁決 6），上級排在下級之前。只由 Demo 產生器建立，不放進 <see cref="EnsureAsync"/>：
    /// 測試基底會呼叫 EnsureAsync，簽核測試自己建組織。
    /// </summary>
    public static readonly (string Code, string Name, string? Parent, string? Manager, string[] Members)[] Departments =
    [
        ("exec", "總經理室", null, "manager01", ["manager01"]),
        ("sales", "業務部", "exec", "manager01", ["sales01", "sales02", "sales03"]),
        ("design", "產品設計部", "exec", "designer01", ["designer01"]),
        ("ops", "營運部", "exec", "ops01", ["ops01", "keeper01"]),
        ("purchasing", "採購部", "ops", null, ["buyer01"]),
        ("production", "生產部", "ops", "planner01", ["planner01", "floor01", "floor02", "maint01", "qa01"])
    ];

    /// <summary>補齊示範資料，已存在的不覆寫；會自行 SaveChanges。</summary>
    public static async Task EnsureAsync(DynamicDbContext db, CancellationToken ct = default)
    {
        var users = await db.Set<AppUser>().Select(u => u.UserId).ToListAsync(ct);
        foreach (var (id, name) in Users.Where(u => !users.Contains(u.Id)))
            db.Set<AppUser>().Add(new AppUser { UserId = id, DisplayName = name, IsDemoAccount = true });

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

    /// <summary>補齊示範組織（先要有 <see cref="Users"/>；createdTime 是營運時間）；已存在的部門與已有部門的使用者不覆寫；會自行 SaveChanges。</summary>
    public static async Task EnsureOrganizationAsync(DynamicDbContext db, DateTime createdTime, CancellationToken ct = default)
    {
        var departments = await db.Set<Department>().ToDictionaryAsync(d => d.Code, ct);
        foreach (var (code, name, parent, manager, _) in Departments.Where(d => !departments.ContainsKey(d.Code)))
        {
            var department = new Department
            {
                Code = code, NameI18n = new I18nText([KeyValuePair.Create("zh-TW", name)]),
                ParentId = parent is null ? null : departments[parent].Id, ManagerUserId = manager, CreatedUserId = "admin",
                CreatedTime = createdTime
            };
            db.Set<Department>().Add(department);
            await db.SaveChangesAsync(ct);   // 下級要用上級的 Id
            departments[code] = department;
        }

        var assigned = await db.Set<UserDepartment>().Select(m => m.UserId).ToListAsync(ct);
        foreach (var (code, _, _, _, members) in Departments)
        foreach (var userId in members.Where(u => !assigned.Contains(u)))
            db.Set<UserDepartment>().Add(new UserDepartment { UserId = userId, DepartmentId = departments[code].Id });
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
        var customers = await db.Set<MockCustomer>().Select(c => c.CustomerNo).ToListAsync(ct);
        conflicts.AddRange(Customers.Where(c => customers.Contains(c.No)).Select(c => $"客戶 {c.No} 已存在"));
        var departments = await db.Set<Department>().Select(d => d.Code).ToListAsync(ct);
        conflicts.AddRange(Departments.Where(d => departments.Contains(d.Code)).Select(d => $"部門 {d.Code} 已存在"));
        return conflicts;
    }
}
