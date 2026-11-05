using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Entities.Mock;
using DynamicModules.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Seed;

public static class SeedData
{
    private static readonly (string Id, string Name)[] Users =
    [
        ("admin", "系統管理員"), ("sales01", "業務一"), ("sales02", "業務二"),
        ("manager01", "業務主管"), ("floor01", "現場人員"),
        ("floor02", "現場人員二"), ("maint01", "保養人員"), ("qa01", "品保人員")
    ];

    private static readonly (string Key, string Name, string[] Members)[] Groups =
    [
        ("mis", "MIS", ["admin"]),
        ("sales", "業務", ["sales01", "sales02"]),
        ("sales-manager", "業務主管", ["manager01"]),
        ("floor", "現場人員", ["floor01", "floor02"]),
        ("maint", "保養", ["maint01"]),
        ("qa", "品保", ["qa01"])
    ];

    /// <summary>第一次升級時，既有模組授權給這三個群組（Read/Add/Edit/Delete/PrintExport/Pricing）。</summary>
    private static readonly string[] UpgradeGrantGroups = ["sales", "sales-manager", "floor"];

    public static async Task EnsureSeededAsync(DynamicDbContext db, CancellationToken ct = default)
    {
        var existing = await db.Set<AppUser>().Select(u => u.UserId).ToListAsync(ct);
        foreach (var (id, name) in Users.Where(u => !existing.Contains(u.Id)))
            db.Set<AppUser>().Add(new AppUser { UserId = id, DisplayName = name });

        if (!await db.Set<DynamicProgram>().AnyAsync(p => p.ProgramCode == "DYN00000", ct))
            db.Set<DynamicProgram>().Add(new DynamicProgram
            {
                ProgramCode = "DYN00000",
                NameI18n = new I18nText([KeyValuePair.Create("zh-TW", "動態模組設計")]),
                Active = true
            });

        await SeedMockEntitiesAsync(db, ct);
        await db.SaveChangesAsync(ct);

        // 建立群組的那次 seed 才是第一次升級；之後新模組、被清空的授權都不補發（fail-closed）。
        var firstUpgrade = !await db.Set<PermissionGroup>().AnyAsync(ct);
        await SeedGroupsAsync(db, ct);

        var admin = await db.Set<AppUser>().SingleAsync(u => u.UserId == "admin", ct);
        admin.IsSystemAdmin = true;

        if (firstUpgrade)
            await GrantExistingModulesAsync(db, ct);

        // 匿名記錄（第 2 階段以前的 OwnerUserId）改歸 admin。
        await db.Set<Record>().Where(r => !db.Set<AppUser>().Any(u => u.UserId == r.OwnerUserId))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.OwnerUserId, "admin"), ct);
        // 匿名使用者的個人檢視同樣歸 admin，否則沒有人看得到（OwnerUserId 為 null 的是共用檢視，不動）。
        await db.Set<ListView>().Where(v => v.OwnerUserId == "anonymous")
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.OwnerUserId, "admin"), ct);

        var modules = await db.Set<Module>().Select(m => new { m.ProgramCode, m.Status }).ToListAsync(ct);
        var programs = await db.Set<DynamicProgram>().Where(p => p.ProgramCode != "DYN00000").ToListAsync(ct);
        foreach (var program in programs)
            program.Active = modules.Any(m => m.ProgramCode == program.ProgramCode && m.Status == ModuleStatus.Active);

        await db.SaveChangesAsync(ct);
    }

    private static readonly (string No, string Name)[] Molds =
    [
        ("M-0001", "外殼上蓋模"), ("M-0002", "外殼下蓋模"), ("M-0003", "按鍵模"), ("M-0004", "電池蓋模"), ("M-0005", "支架模"),
        ("M-0006", "射出模"), ("M-0007", "側蓋模"), ("M-0008", "旋鈕模"), ("M-0009", "面板模"),
        ("M-0010", "底座模"), ("M-0011", "導光板模"), ("M-0012", "連接器模")
    ];

    private static readonly (string No, string Name)[] Customers =
        [
            ("C-001", "台灣精密"), ("C-002", "恆川機械"), ("C-003", "曜陽電子"),
            ("C-004", "岳峰精機"), ("C-005", "澄川塑膠"), ("C-006", "瑞嶺零件"), ("C-007", "拓明模具"), ("C-008", "禾晟電機")
        ];

    /// <summary>mock 既有實體：依主鍵存在與否新增，不覆寫既有資料。</summary>
    private static async Task SeedMockEntitiesAsync(DynamicDbContext db, CancellationToken ct)
    {
        var molds = await db.Set<MockMold>().Select(m => m.MoldNo).ToListAsync(ct);
        foreach (var (no, name) in Molds.Where(m => !molds.Contains(m.No)))
            db.Set<MockMold>().Add(new MockMold { MoldNo = no, Name = name });
        var customers = await db.Set<MockCustomer>().Select(c => c.CustomerNo).ToListAsync(ct);
        foreach (var (no, name) in Customers.Where(c => !customers.Contains(c.No)))
            db.Set<MockCustomer>().Add(new MockCustomer { CustomerNo = no, Name = name });
    }

    private static async Task SeedGroupsAsync(DynamicDbContext db, CancellationToken ct)
    {
        var groups = await db.Set<PermissionGroup>().ToListAsync(ct);
        foreach (var (key, name, _) in Groups.Where(g => groups.All(x => x.Key != g.Key)))
        {
            var group = new PermissionGroup
            {
                Key = key,
                NameI18n = new I18nText([KeyValuePair.Create("zh-TW", name)])
            };
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

    private static async Task GrantExistingModulesAsync(DynamicDbContext db, CancellationToken ct)
    {
        var codes = await db.Set<Module>().Select(m => m.ProgramCode).ToListAsync(ct);
        var groupIds = await db.Set<PermissionGroup>()
            .Where(g => UpgradeGrantGroups.Contains(g.Key)).Select(g => g.Id).ToListAsync(ct);
        foreach (var code in codes)
            foreach (var groupId in groupIds)
                db.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
                {
                    GroupId = groupId, ProgramCode = code,
                    CanRead = true, CanAdd = true, CanEdit = true, CanDelete = true, CanPrint = true, CanAmt = true
                });
    }
}
