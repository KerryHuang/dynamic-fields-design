namespace DynamicModules.Application.Dynamic.AccountService.Data;

/// <summary>登入、改密碼成功的結果；SecurityStamp 只給 controller 簽發 cookie，不回給前端。</summary>
public record AuthUserResponse(string UserId, string DisplayName, bool MustChangePassword, string SecurityStamp);

public record DemoAccountResponse(string UserId, string DisplayName);

/// <summary>登入頁選項；未開啟 Demo 一鍵登入時 DemoAccounts 是空陣列。</summary>
/// <summary>
/// 登入前的選項。<c>DefaultLocale</c>、<c>EnabledLocales</c> 是語系政策的生效值（i4a 之後只再加欄位，不改這兩欄的形狀）；
/// <c>Locales</c> 是啟用語系的自稱、Quasar 語言包與上層語系，順序同已安裝語系（語系包 i4a 計畫判斷 12）。
/// </summary>
public record AuthOptionsResponse(
    bool DemoLoginEnabled, bool DemoGenerating, List<DemoAccountResponse> DemoAccounts, string DefaultLocale, IReadOnlyList<string> EnabledLocales,
    IReadOnlyList<Common.LocaleInfoResponse> Locales);

/// <summary>系統管理 → 使用者的一列；不含密碼雜湊與 SecurityStamp。</summary>
public record AdminUserResponse(
    string UserId, string DisplayName, bool IsActive, bool IsSystemAdmin, bool IsDemoAccount, bool IsLocked, List<long> GroupIds,
    long? DepartmentId = null,
    // 權限軸設計 §8：個人直接設定（編輯對話框只載入與存回它）、有效結果（和 GET me 同一套算法）、提供系統權限的來源
    Common.Security.SystemPrivilegesDto? SystemPrivileges = null, Common.Security.SystemPrivilegesDto? EffectiveSystemPrivileges = null,
    List<SystemPrivilegeSourceResponse>? SystemPrivilegeSources = null);

/// <summary>系統權限的來源：<c>group</c>（有任一系統權限的群組）或 <c>systemAdmin</c>（其餘欄位不輸出）。</summary>
public record SystemPrivilegeSourceResponse(
    string Kind, long? GroupId = null, string? GroupName = null, Common.Security.DesignerFlags? Designer = null, bool? PermissionAdmin = null);

/// <summary>系統管理 → 群組的一列。</summary>
public record AdminGroupResponse(long Id, string Key, DynamicModules.Domain.ValueObjects.I18nText NameI18n, List<string> MemberUserIds,
    Common.Security.SystemPrivilegesDto? SystemPrivileges = null);

/// <summary>刪除群組前的影響；多了該群組的系統權限直接設定（權限軸設計 §8）。</summary>
public record GroupImpactResponse(
    int MemberCount, List<string> Modules, int FieldPermissionCount, int DataScopeCount,
    List<DynamicModules.Domain.Repositories.Dynamic.BlockingOption> BlockingOptions, List<string> Reports, List<string> Dashboards,
    Common.Security.SystemPrivilegesDto SystemPrivileges);
