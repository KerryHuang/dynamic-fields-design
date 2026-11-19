using DynamicModules.Domain.Authorization;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.TestSupport;

/// <summary>
/// 權限軸升級帶入的資料（權限軸設計 §18 PAX-02，測試資料全部虛構），ApplicationTests 與 WebAPITests 共用（PAX-01b、02、02b、02c、03）：
/// 寫進一個「已到最新 schema、還沒跑過新版 SeedData」的資料庫，模擬上一版升級前的狀態（只有 DYN00000 的設計器權限；
/// SYS00001 的 program 列是舊版 SeedData 退回時留下的 Active = false，另有兩列舊版不認的 SYS00001 殘列）。
/// <list type="bullet">
/// <item>G1 DYN00000 讀改、G2 只讀、G3 只改（沒有讀）、G5 沒有 DYN00000 但有殘列 SYS00001 CanEdit = true、
/// G6 DYN00000 改＋殘列 SYS00001 CanEdit = false、G7 只新增。</item>
/// <item>u1 個人 DYN00000 改；u2 個人只新增；u4 在 G2、G7 且個人讀取；u5 已停用、個人 DYN00000 改；u8 在 G2 與 G1。
/// g3m、g5m、g6m 分別是 G3、G5、G6 的成員。</item>
/// <item>業務模組 program <see cref="BusinessCode"/> 的群組列與個人列若干。</item>
/// </list>
/// </summary>
public static class PermissionAxesUpgradeData
{
    public const string BusinessCode = "DYN09001";

    public static readonly string[] Groups = ["pax-g1", "pax-g2", "pax-g3", "pax-g5", "pax-g6", "pax-g7"];

    public static readonly string[] Users = ["u1", "u2", "u4", "u5", "u8", "g3m", "g5m", "g6m"];

    /// <summary>升級後該有 SYS00001 的群組與個人（K1：每一列 DYN00000 CanEdit = true 都帶入）。</summary>
    public static readonly string[] ImportedGroups = ["pax-g1", "pax-g3", "pax-g6"];

    public static readonly string[] ImportedUsers = ["u1", "u5"];

    /// <summary>寫入資料（在測試許可內存檔）；回傳群組代號 → id。</summary>
    public static async Task<Dictionary<string, long>> InsertAsync(DynamicDbContext db)
    {
        if (!await db.Set<AppUser>().AnyAsync(u => u.UserId == "admin"))
            db.Set<AppUser>().Add(new AppUser { UserId = "admin", DisplayName = "系統管理員", IsSystemAdmin = true });
        foreach (var code in new[] { SystemProgramCodes.Designer, SystemProgramCodes.PermissionAdmin, BusinessCode })
            if (!await db.Set<DynamicProgram>().AnyAsync(p => p.ProgramCode == code))
                db.Set<DynamicProgram>().Add(new DynamicProgram
                {
                    ProgramCode = code, NameI18n = new I18nText([KeyValuePair.Create("zh-TW", code)]),
                    Active = code != SystemProgramCodes.PermissionAdmin,
                });
        foreach (var id in Users)
            db.Set<AppUser>().Add(new AppUser { UserId = id, DisplayName = id, IsActive = id != "u5" });
        var groups = Groups.ToDictionary(k => k, k => new PermissionGroup { Key = k, NameI18n = new I18nText([KeyValuePair.Create("zh-TW", k)]) });
        db.Set<PermissionGroup>().AddRange(groups.Values);
        await db.SaveChangesAsync();
        var g = groups.ToDictionary(x => x.Key, x => x.Value.Id);

        void Member(string group, string user) => db.Set<UserGroupMapping>().Add(new UserGroupMapping { GroupId = g[group], UserId = user });
        Member("pax-g2", "u4");
        Member("pax-g7", "u4");
        Member("pax-g2", "u8");
        Member("pax-g1", "u8");
        Member("pax-g3", "g3m");
        Member("pax-g5", "g5m");
        Member("pax-g6", "g6m");

        GroupProgramPrivilege G(string group, string code) => new() { GroupId = g[group], ProgramCode = code };
        UserProgramPrivilege U(string user, string code) => new() { UserId = user, ProgramCode = code };
        db.Set<GroupProgramPrivilege>().AddRange(
            With(G("pax-g1", SystemProgramCodes.Designer), read: true, edit: true),
            With(G("pax-g2", SystemProgramCodes.Designer), read: true),
            With(G("pax-g3", SystemProgramCodes.Designer), edit: true),
            With(G("pax-g5", SystemProgramCodes.PermissionAdmin), edit: true),
            With(G("pax-g6", SystemProgramCodes.Designer), edit: true),
            With(G("pax-g6", SystemProgramCodes.PermissionAdmin)),
            With(G("pax-g7", SystemProgramCodes.Designer), add: true),
            With(G("pax-g1", BusinessCode), read: true, add: true, edit: true),
            With(G("pax-g2", BusinessCode), read: true));
        db.Set<UserProgramPrivilege>().AddRange(
            With(U("u1", SystemProgramCodes.Designer), edit: true),
            With(U("u2", SystemProgramCodes.Designer), add: true),
            With(U("u4", SystemProgramCodes.Designer), read: true),
            With(U("u5", SystemProgramCodes.Designer), edit: true),
            With(U("u2", BusinessCode), read: true, edit: true));
        await db.SaveForTestsAsync();
        return g;
    }

    private static T With<T>(T p, bool read = false, bool add = false, bool edit = false) where T : ProgramPrivilege
    {
        (p.CanRead, p.CanAdd, p.CanEdit) = (read, add, edit);
        return p;
    }

    /// <summary>守衛表逐列的文字快照（排序後）；<paramref name="withoutPermissionAdmin"/> 時略過 SYS00001 的列（帶入只動它們）。</summary>
    public static List<string> GuardRows(DynamicDbContext db, bool withoutPermissionAdmin = false) =>
    [
        .. Of<GroupProgramPrivilege>(db, withoutPermissionAdmin), .. Of<UserProgramPrivilege>(db, withoutPermissionAdmin),
        .. Of<FieldPermission>(db, false), .. Of<ModuleDataScope>(db, false), .. Of<FieldOptionScope>(db, false), .. Of<FieldGrant>(db, false),
        .. Of<AppGrant>(db, false), .. Of<ModuleActionScope>(db, false), .. Of<UserGroupMapping>(db, false),
    ];

    private static IEnumerable<string> Of<T>(DynamicDbContext db, bool withoutPermissionAdmin) where T : class
    {
        var props = db.Model.FindEntityType(typeof(T))!.GetProperties().Where(p => p.PropertyInfo is not null).ToList();
        return db.Set<T>().AsNoTracking().ToList()
            .Where(r => !withoutPermissionAdmin || r is not ProgramPrivilege { ProgramCode: SystemProgramCodes.PermissionAdmin })
            .Select(r => typeof(T).Name + ":" + string.Join("|", props.Select(p => $"{p.Name}={p.PropertyInfo!.GetValue(r)}")))
            .Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>SYS00001 列的持有者：群組以代號、個人以 userId 列出，並確認每一列都只有 CanEdit。</summary>
    public static async Task<(List<string> Groups, List<string> Users)> PermissionAdminHoldersAsync(DynamicDbContext db)
    {
        var groupRows = await db.Set<GroupProgramPrivilege>().AsNoTracking().Where(p => p.ProgramCode == SystemProgramCodes.PermissionAdmin).ToListAsync();
        var userRows = await db.Set<UserProgramPrivilege>().AsNoTracking().Where(p => p.ProgramCode == SystemProgramCodes.PermissionAdmin).ToListAsync();
        foreach (var r in groupRows.Cast<ProgramPrivilege>().Concat(userRows))
            if (!r.CanEdit || r.CanRead || r.CanAdd || r.CanDelete || r.CanPrint || r.CanAmt || r.CanApprove)
                throw new InvalidOperationException($"SYS00001 列只能有 CanEdit：{r.ProgramCode}");
        var keys = await db.Set<PermissionGroup>().AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Key);
        return (groupRows.Select(r => keys[r.GroupId]).Order(StringComparer.Ordinal).ToList(),
            userRows.Select(r => r.UserId).Order(StringComparer.Ordinal).ToList());
    }
}
