using System.Globalization;
using DynamicModules.Domain.Authorization;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Seed;

/// <summary>
/// 升級帶入（權限軸設計 §6.3，M2、M10、T12）：不用 EF migration，啟動時以 <see cref="SystemSetting.PermissionAxesImportedOn"/> 守門，只做一次。
/// 對每一列 DYN00000 CanEdit = true 的群組列、個人列插入同對象的 SYS00001（只有 CanEdit），之前先清掉既存的 SYS00001 列（舊版不認這個代碼）。
/// 不動系統管理員、DYN00000 的任何列、業務模組的任何列。由 <see cref="SeedData"/> 在 Seed 寫入許可內呼叫。
/// </summary>
public static class PermissionAxesImport
{
    public static async Task RunAsync(DynamicDbContext db, CancellationToken ct = default)
    {
        // EF 對 SQLite 開 BEGIN IMMEDIATE、PostgreSQL 由全庫寫入鎖排隊：多個實例同時啟動時依序進來，後到的看得到標記
        // 已在外層交易內（測試或呼叫端自己包）時沿用，由外層提交
        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        if (await db.Set<SystemSetting>().AnyAsync(s => s.Key == SystemSetting.PermissionAxesImportedOn, ct)) return;

        // 追蹤式寫入：每一列照樣經攔截器檢查
        db.Set<GroupProgramPrivilege>().RemoveRange(
            await db.Set<GroupProgramPrivilege>().Where(p => p.ProgramCode == SystemProgramCodes.PermissionAdmin).ToListAsync(ct));
        db.Set<UserProgramPrivilege>().RemoveRange(
            await db.Set<UserProgramPrivilege>().Where(p => p.ProgramCode == SystemProgramCodes.PermissionAdmin).ToListAsync(ct));
        await db.SaveChangesAsync(ct);

        var groups = await db.Set<GroupProgramPrivilege>().AsNoTracking()
            .Where(p => p.ProgramCode == SystemProgramCodes.Designer && p.CanEdit).Select(p => p.GroupId).ToListAsync(ct);
        var users = await db.Set<UserProgramPrivilege>().AsNoTracking()
            .Where(p => p.ProgramCode == SystemProgramCodes.Designer && p.CanEdit).Select(p => p.UserId).ToListAsync(ct);
        db.Set<GroupProgramPrivilege>().AddRange(groups.Distinct().Select(id =>
            new GroupProgramPrivilege { GroupId = id, ProgramCode = SystemProgramCodes.PermissionAdmin, CanEdit = true }));
        db.Set<UserProgramPrivilege>().AddRange(users.Distinct().Select(id =>
            new UserProgramPrivilege { UserId = id, ProgramCode = SystemProgramCodes.PermissionAdmin, CanEdit = true }));
        db.Set<SystemSetting>().Add(new SystemSetting
        {
            Key = SystemSetting.PermissionAxesImportedOn,
            Value = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        });
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
    }
}
