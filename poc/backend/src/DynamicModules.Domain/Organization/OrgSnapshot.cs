namespace DynamicModules.Domain.Organization;

/// <summary>主管解析用的部門資料（不含名稱）。</summary>
public sealed record OrgDepartment(long Id, long? ParentId, string? ManagerUserId, bool Active);

/// <summary>某一刻的組織：部門、使用者所屬部門、啟用中的使用者。單次請求內載入，不跨請求快取。</summary>
public sealed record OrgSnapshot(
    IReadOnlyDictionary<long, OrgDepartment> Departments,
    IReadOnlyDictionary<string, long> UserDepartment,
    IReadOnlySet<string> ActiveUsers);
