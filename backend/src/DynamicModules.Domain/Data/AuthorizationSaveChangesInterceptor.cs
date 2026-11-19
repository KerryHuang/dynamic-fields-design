using DynamicModules.Domain.Authorization;
using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DynamicModules.Domain.Data;

/// <summary>
/// 授權寫入邊界的追蹤式寫入檢查（權限軸設計 §7.4）：每次 SaveChanges 先 DetectChanges，再逐列看守衛表的 Added／Modified／Deleted
/// 與 <c>Dynamic_Field.VisibilityRestricted</c> 改成 false，照目前的寫入許可判斷，不符就丟例外。
/// 由 <see cref="DynamicDbContext"/> 的 OnConfiguring 掛上，兩個 provider、測試自組的 context 都一樣，沒有忘記註冊的可能。
/// </summary>
public sealed class AuthorizationSaveChangesInterceptor : SaveChangesInterceptor
{
    public static readonly AuthorizationSaveChangesInterceptor Instance = new();

    private AuthorizationSaveChangesInterceptor() { }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } context) Check(context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context) Check(context);
        return ValueTask.FromResult(result);
    }

    /// <summary>未存的守衛變更（寫入許可結束時的檢查用）。</summary>
    public static bool HasPendingGuardChanges(DbContext context)
    {
        context.ChangeTracker.DetectChanges();
        return GuardedEntries(context).Any();
    }

    private static void Check(DbContext context)
    {
        // EF 在 SavingChanges 之後才偵測變更；不先做會漏掉只改屬性、沒標記的變更
        context.ChangeTracker.DetectChanges();
        var entries = GuardedEntries(context).ToList();
        if (entries.Count == 0) return;
        var permit = AuthorizationWriteScope.Current;
        foreach (var entry in entries)
            if (Reject(permit, entry) is { } why)
                throw new InvalidOperationException(
                    $"授權資料的寫入被拒（{entry.Metadata.GetTableName()}，{entry.State}）：{why}");
    }

    private static IEnumerable<EntityEntry> GuardedEntries(DbContext context) =>
        context.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                                                   && (IsGuardTable(e) || RelaxesFieldVisibility(e)));

    private static bool IsGuardTable(EntityEntry e) => e.Metadata.GetTableName() is { } t && AuthorizationTables.Guarded.Contains(t);

    /// <summary>
    /// 欄位定義的守衛欄：屬性被標為已修改且新值是放寬的值（false）。不靠原值，離線實體 Update 的原值不可信時一樣擋（寧可誤擋）。
    /// 新增欄位、整列刪除不放寬既有可見性，不在此限。
    /// </summary>
    private static bool RelaxesFieldVisibility(EntityEntry e) =>
        e is { State: EntityState.Modified, Entity: Field field }
        && !field.VisibilityRestricted
        && e.Property(nameof(Field.VisibilityRestricted)).IsModified;

    private static string? Reject(AuthorizationPermit? permit, EntityEntry e)
    {
        if (permit is null) return "沒有寫入許可（寫授權資料的請求要宣告 IPermissionAdminRequest 或經 PermissionAdminGate）";
        return permit.Kind switch
        {
            AuthorizationPermitKind.Seed or AuthorizationPermitKind.Test => null,
            AuthorizationPermitKind.Authorized => RowRule(permit, e),
            AuthorizationPermitKind.Derived => DerivedRule(permit, e),
            AuthorizationPermitKind.Cleanup => CleanupRule(permit, e),
            _ => "未知的許可種類",
        };
    }

    /// <summary>列層規則：系統代碼列與成員表只准在系統管理員請求內寫（許可帶 SystemAdminVerified）。</summary>
    private static string? RowRule(AuthorizationPermit permit, EntityEntry e)
    {
        if (permit.SystemAdminVerified) return null;
        if (e.Entity is UserGroupMapping) return "群組成員只准在系統管理員請求內寫";
        if (e.Entity is ProgramPrivilege p && (SystemProgramCodes.All.Contains(p.ProgramCode) || OriginalIsSystemCode(e)))
            return "系統權限代碼的授權列只准在系統管理員請求內寫";
        return null;
    }

    private static bool OriginalIsSystemCode(EntityEntry e) =>
        e.State != EntityState.Added
        && e.OriginalValues[nameof(ProgramPrivilege.ProgramCode)] is string code && SystemProgramCodes.All.Contains(code);

    /// <summary>Derived 只准新增 SourceKind = App、欄位／應用程式／群組都在許可參數內的 FieldGrant。</summary>
    private static string? DerivedRule(AuthorizationPermit permit, EntityEntry e) =>
        e is { State: EntityState.Added, Entity: FieldGrant g }
        && g.SourceKind == GrantSource.App && g.AppId is { } app
        && permit.FieldIds.Contains(g.FieldId) && permit.AppIds.Contains(app) && permit.GroupIds.Contains(g.GroupId)
            ? null
            : "衍生許可只准新增登記欄位、應用程式、群組的 App 來源可見列";

    /// <summary>Cleanup 只准刪除屬於目標（模組、它的 program 代碼、欄位、選項）的列。</summary>
    private static string? CleanupRule(AuthorizationPermit permit, EntityEntry e)
    {
        if (e.State != EntityState.Deleted) return "清除許可只准刪除";
        var belongs = e.Entity switch
        {
            ProgramPrivilege p => permit.ProgramCodes.Contains(p.ProgramCode),
            ModuleDataScope s => permit.ModuleIds.Contains(s.ModuleId),
            ModuleActionScope s => permit.ModuleIds.Contains(s.ModuleId),
            AppGrant g => permit.ModuleIds.Contains(g.ModuleId),
            FieldPermission p => permit.FieldIds.Contains(p.FieldId),
            FieldGrant g => permit.FieldIds.Contains(g.FieldId),
            FieldOptionScope s => permit.OptionIds.Contains(s.OptionId),
            _ => false,
        };
        return belongs ? null : "清除許可只准刪除屬於目標模組或欄位的列";
    }
}
