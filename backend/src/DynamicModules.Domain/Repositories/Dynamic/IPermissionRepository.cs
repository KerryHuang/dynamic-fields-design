using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

public interface IPermissionRepository
{
    /// <summary>載入該模組、該使用者所屬群組的權限列；使用者不存在回 null。</summary>
    Task<PermissionInputs?> LoadInputsAsync(string userId, Module module, CancellationToken ct);

    /// <summary>只載入單一 ProgramCode 的動作軸權限（設計器 DYN00000 用）；使用者不存在回 null。</summary>
    Task<PermissionInputs?> LoadProgramInputsAsync(string userId, string programCode, CancellationToken ct);

    Task<List<PermissionGroup>> ListGroupsAsync(CancellationToken ct);

    Task<List<UserGroupMapping>> ListMembershipsAsync(CancellationToken ct);

    Task<List<AppUser>> ListUsersAsync(CancellationToken ct);

    Task<AppUser?> GetUserAsync(string userId, CancellationToken ct);

    /// <summary>該模組的全部權限設定列。</summary>
    Task<ModulePermissionRows> LoadModuleRowsAsync(Module module, CancellationToken ct);

    /// <summary>刪除該模組的全部權限列再寫入新列；需在交易內呼叫，寫入由 IUnitOfWork 提交。</summary>
    Task ReplaceModuleRowsAsync(Module module, ModulePermissionRows rows, CancellationToken ct);

    /// <summary>全部系統代碼（設計器、權限設定）的群組列與個人列，不追蹤、已具體化（權限軸設計 §8、§9）。</summary>
    Task<SystemPrivilegeRows> LoadSystemPrivilegeRowsAsync(CancellationToken ct);

    /// <summary>
    /// 把一個群組（<paramref name="groupId"/>）或一位使用者（<paramref name="userId"/>）的系統代碼列整份換成 <paramref name="desired"/>
    /// （只含要保留的列；沒列到的系統代碼就刪除）。追蹤式寫入，存檔由呼叫端在寫入許可內做。
    /// </summary>
    Task ReplaceSystemRowsAsync(long? groupId, string? userId, IReadOnlyList<ProgramPrivilege> desired, CancellationToken ct);
}

/// <summary>系統代碼的授權列。</summary>
public sealed record SystemPrivilegeRows(List<GroupProgramPrivilege> Groups, List<UserProgramPrivilege> Users);
