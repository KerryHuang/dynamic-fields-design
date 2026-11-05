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
}
