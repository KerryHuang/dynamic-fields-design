using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.OrganizationService;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Organization;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Tasks;

namespace DynamicModules.Application.Dynamic.TaskService;

/// <summary>
/// 算出一位使用者的任務可見條件（看板設計 §4.4）：部屬名單（政策 task.supervisorScope 關閉時不載入組織）與各可讀模組的資料範圍。
/// 同一個請求內依使用者快取；讀不到就當成沒有（fail closed）。一個 scope 跑多個請求時（Demo 產生器、測試），
/// 每個請求之間要呼叫 <see cref="Reset"/>（<c>DemoStructure.ResetPerRequestState</c> 會呼叫）。
/// </summary>
public sealed class TaskVisibilityService(ICurrentUser user, IPolicyReader policies, OrgSnapshotLoader org,
    IPermissionRepository permissions, IModuleRepository modules, IDynamicAccessGuard guard)
{
    private readonly Dictionary<string, IReadOnlySet<string>> _subordinates = [];
    private readonly Dictionary<string, TaskVisibility> _visibility = [];

    /// <summary>清掉快取，下一次重新讀政策與組織。</summary>
    public void Reset()
    {
        _subordinates.Clear();
        _visibility.Clear();
    }

    /// <summary>目前使用者的部屬名單；政策關閉或沒有部屬時是空集合。</summary>
    public async Task<IReadOnlySet<string>> SubordinatesAsync(CancellationToken ct)
    {
        var me = user.UserId;
        if (_subordinates.TryGetValue(me, out var cached)) return cached;
        IReadOnlySet<string> result = new HashSet<string>();
        if (await policies.BoolAsync(PolicyCatalog.TaskSupervisorScope, null, ct))
        {
            var recursive = await policies.BoolAsync(PolicyCatalog.TaskSupervisorScopeRecursive, null, ct);
            result = Subordinates.Of(await org.LoadAsync(ct), me, recursive);
        }
        _subordinates[me] = result;
        return result;
    }

    public async Task<TaskVisibility> ForCurrentUserAsync(CancellationToken ct)
    {
        var me = user.UserId;
        if (_visibility.TryGetValue(me, out var cached)) return cached;
        var admin = await permissions.GetUserAsync(me, ct) is { IsActive: true, IsSystemAdmin: true };
        var scopes = new List<ModuleReadScope>();
        if (!admin)
            foreach (var module in await modules.ListReadableAsync(ct))
            {
                if (ModuleRelations.IsDetail(module)) continue;
                EffectivePermissions? read;
                try { read = (await guard.AuthorizeAsync(module, PermissionAction.Read, ct)).Permissions; }
                catch (Exception e) when (e is not OperationCanceledException) { read = null; }   // fail closed：這個模組不進條件
                if (read is not null) scopes.Add(new ModuleReadScope(module.Id, read.OwnerUserIds));
            }
        var result = new TaskVisibility(admin, me, admin ? new HashSet<string>() : await SubordinatesAsync(ct), scopes);
        _visibility[me] = result;
        return result;
    }
}
