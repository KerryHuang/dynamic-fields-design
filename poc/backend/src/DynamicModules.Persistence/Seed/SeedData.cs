using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Seed;

public static class SeedData
{
    public static async Task EnsureSeededAsync(DynamicDbContext db, CancellationToken ct = default)
    {
        // 沒有任何系統管理員時才建 admin；有人（含改名後的管理員）就不動。
        if (!await db.Set<AppUser>().AnyAsync(u => u.IsSystemAdmin, ct))
        {
            var admin = await db.Set<AppUser>().SingleOrDefaultAsync(u => u.UserId == "admin", ct);
            if (admin is null) db.Set<AppUser>().Add(new AppUser { UserId = "admin", DisplayName = "系統管理員", IsSystemAdmin = true });
            else admin.IsSystemAdmin = true;
        }

        if (!await db.Set<DynamicProgram>().AnyAsync(p => p.ProgramCode == "DYN00000", ct))
            db.Set<DynamicProgram>().Add(new DynamicProgram
            {
                ProgramCode = "DYN00000",
                NameI18n = new I18nText([KeyValuePair.Create("zh-TW", "動態模組設計")]),
                Active = true
            });

        await db.SaveChangesAsync(ct);

        // 匿名記錄（第 2 階段以前的 OwnerUserId）改歸 admin。
        await db.Set<Record>().Where(r => !db.Set<AppUser>().Any(u => u.UserId == r.OwnerUserId))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.OwnerUserId, "admin"), ct);
        // 匿名使用者的個人檢視同樣歸 admin，否則沒有人看得到（OwnerUserId 為 null 的是共用檢視，不動）。
        await db.Set<ListView>().Where(v => v.OwnerUserId == "anonymous")
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.OwnerUserId, "admin"), ct);

        var modules = await db.Set<Module>().Select(m => new { m.ProgramCode, m.Status }).ToListAsync(ct);
        var programs = await db.Set<DynamicProgram>().Where(p => p.ProgramCode != "DYN00000").ToListAsync(ct);
        foreach (var program in programs)
            program.Active = modules.Any(m => m.ProgramCode == program.ProgramCode && m.Status is ModuleStatus.Active or ModuleStatus.Closed);

        await db.SaveChangesAsync(ct);
    }
}
