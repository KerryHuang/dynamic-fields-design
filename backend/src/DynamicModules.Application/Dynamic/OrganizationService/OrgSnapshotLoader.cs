using DynamicModules.Domain.Organization;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.OrganizationService;

/// <summary>載入主管解析用的組織快照；單次呼叫內載入，不跨請求快取（組織異動立即生效，設計 §11.4）。</summary>
public sealed class OrgSnapshotLoader(IOrganizationRepository org, IPermissionRepository permissions)
{
    public async Task<OrgSnapshot> LoadAsync(CancellationToken ct)
    {
        var departments = (await org.ListAsync(ct))
            .ToDictionary(d => d.Id, d => new OrgDepartment(d.Id, d.ParentId, d.ManagerUserId, d.Active));
        var members = await org.ListUserDepartmentsAsync(ct);
        var active = (await permissions.ListUsersAsync(ct)).Where(u => u.IsActive).Select(u => u.UserId).ToHashSet();
        return new OrgSnapshot(departments, members, active);
    }
}
