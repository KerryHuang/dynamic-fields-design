using System.Security.Cryptography;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.WebAPI.Security;

/// <summary>
/// 資料庫裡沒有「啟用中且已設密碼的系統管理員」時，建立或補齊 admin。已有就什麼都不做，
/// 所以之後改環境變數不會覆寫密碼。
/// </summary>
public static class AdminBootstrap
{
    private const string PasswordChars = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static async Task EnsureAsync(
        DynamicDbContext db, IPasswordService passwords, IConfiguration config, ILogger logger, CancellationToken ct = default)
    {
        if (await db.Set<AppUser>().AnyAsync(u => u.IsActive && u.IsSystemAdmin && u.PasswordHash != null, ct)) return;

        var password = config["Auth:InitialAdminPassword"];
        var generated = string.IsNullOrWhiteSpace(password);
        if (generated) password = RandomNumberGenerator.GetString(PasswordChars, 16);

        var admin = await db.Set<AppUser>().SingleOrDefaultAsync(u => u.UserId == "admin", ct);
        if (admin is null) db.Set<AppUser>().Add(admin = new AppUser { UserId = "admin", DisplayName = "系統管理員" });
        admin.IsActive = true;
        admin.IsSystemAdmin = true;
        admin.PasswordHash = passwords.Hash(password!);
        admin.MustChangePassword = !config.GetValue<bool>("Demo:LoginEnabled");
        admin.SecurityStamp = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync(ct);

        if (generated) logger.LogWarning("已產生初始管理員密碼（只顯示這一次，登入後必須變更）：{Password}", password);
        else logger.LogInformation("已用 Auth:InitialAdminPassword 設定管理員 admin 的初始密碼");
    }
}
