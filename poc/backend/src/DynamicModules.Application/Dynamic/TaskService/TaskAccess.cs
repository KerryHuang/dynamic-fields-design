using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.TaskService;

/// <summary>Accessible=false 時其餘全是 null：看不到記錄的人連模組代號、記錄 Id、標題都拿不到（設計 §7）。</summary>
public sealed record TaskRecordRef(bool Accessible, string? ModuleKey, long? RecordId, string? Title, bool Deleted);

public sealed record TaskPermissions(bool CanSee, bool CanEdit, bool CanComment, bool CanDelete, bool Frozen, TaskRecordRef? Record);

public sealed record RecordTaskAccess(Record Record, Module Module, bool CanAdd, bool CanEditTasks, bool TitleVisible);

/// <summary>
/// 任務的檢視與修改權限（設計 §7）。看得到：系統管理員、參與者、看得到記錄的人（Read 加資料範圍，已刪除的記錄用 stub 判斷）。
/// 不保留跨呼叫的狀態：模組權限只在單次 <see cref="EvaluateManyAsync"/> 內快取。
/// </summary>
public sealed class TaskAccess(ITaskRepository tasks, IModuleRepository modules, IRecordRepository records,
    IDynamicAccessGuard guard, IPermissionRepository permissions, ICurrentUser user)
{
    private static readonly TaskRecordRef Hidden = new(false, null, null, null, false);

    private sealed record ModuleAccess(Module Module, EffectivePermissions? Read, EffectivePermissions? Edit);

    public async Task<TaskPermissions> EvaluateAsync(TaskItem task, CancellationToken ct) =>
        (await EvaluateManyAsync([task], ct))[0];

    public async Task<IReadOnlyList<TaskPermissions>> EvaluateManyAsync(IReadOnlyList<TaskItem> items, CancellationToken ct)
    {
        var admin = await IsSystemAdminAsync(ct);
        var byModule = new Dictionary<long, ModuleAccess?>();
        var result = new List<TaskPermissions>(items.Count);
        foreach (var task in items)
            result.Add(await EvaluateOneAsync(task, admin, byModule, ct));
        return result;
    }

    private async Task<TaskPermissions> EvaluateOneAsync(
        TaskItem task, bool admin, Dictionary<long, ModuleAccess?> byModule, CancellationToken ct)
    {
        var me = user.UserId;
        var participant = task.Participants.Any(p => p.UserId == me);
        var creator = task.CreatedBy == me;
        var assignee = task.AssigneeUserId == me;

        TaskRecordRef? recordRef = null;
        var recordVisible = false;
        var recordEditable = false;
        var frozen = false;
        if (task.RecordId is { } recordId)
        {
            var stub = await tasks.GetRecordStubAsync(recordId, ct);
            var access = stub is null ? null : await ModuleAccessAsync(stub.ModuleId, byModule, ct);
            frozen = access is null || !access.Module.IsWritable();
            recordVisible = stub is not null && access?.Read is { } read && InScope(read, stub.OwnerUserId);
            recordEditable = recordVisible && access?.Edit is { } edit && InScope(edit, stub!.OwnerUserId);
            recordRef = recordVisible
                ? new TaskRecordRef(true, access!.Module.Key, stub!.Id,
                    access.Module.TitleVisibleTo(access.Read!) ? (stub.Deleted ? task.RecordTitleSnapshot : stub.Title) : null,
                    stub.Deleted)
                : Hidden;
        }

        var canSee = admin || participant || recordVisible;
        if (!canSee) return new TaskPermissions(false, false, false, false, frozen, Hidden);
        var canEdit = !frozen && (creator || assignee || admin || recordEditable);
        var canDelete = task.RuleId is null && !frozen && (creator || admin);
        return new TaskPermissions(true, canEdit, !frozen, canDelete, frozen, recordRef);
    }

    /// <summary>null 代表 404：記錄不存在、屬於明細模組、模組不可讀、沒有 Read、記錄在範圍外。</summary>
    public async Task<RecordTaskAccess?> ForRecordAsync(long recordId, CancellationToken ct)
    {
        var record = await records.GetAsync(recordId, ct);
        if (record is null) return null;
        var module = await modules.GetByIdAsync(record.ModuleId, includeChildren: true, ct);
        if (!module.IsReadable() || ModuleRelations.IsDetail(module)) return null;
        var read = (await guard.AuthorizeAsync(module, PermissionAction.Read, ct)).Permissions;
        if (read is null || !InScope(read, record.OwnerUserId)) return null;
        var edit = (await guard.AuthorizeAsync(module, PermissionAction.Edit, ct)).Permissions;
        var canEditTasks = edit is not null && InScope(edit, record.OwnerUserId);
        return new RecordTaskAccess(record, module, module.IsWritable(), canEditTasks, module.TitleVisibleTo(read));
    }

    /// <summary>啟用中且是系統管理員。</summary>
    public async Task<bool> IsSystemAdminAsync(CancellationToken ct) =>
        await permissions.GetUserAsync(user.UserId, ct) is { IsActive: true, IsSystemAdmin: true };

    private async Task<ModuleAccess?> ModuleAccessAsync(long moduleId, Dictionary<long, ModuleAccess?> byModule, CancellationToken ct)
    {
        if (byModule.TryGetValue(moduleId, out var cached)) return cached;
        var module = await modules.GetByIdAsync(moduleId, includeChildren: true, ct);
        ModuleAccess? access = null;
        if (module.IsReadable() && !ModuleRelations.IsDetail(module))
            access = new ModuleAccess(module,
                (await guard.AuthorizeAsync(module, PermissionAction.Read, ct)).Permissions,
                (await guard.AuthorizeAsync(module, PermissionAction.Edit, ct)).Permissions);
        else if (module is not null)
            access = new ModuleAccess(module, null, null);
        byModule[moduleId] = access;
        return access;
    }

    private static bool InScope(EffectivePermissions p, string ownerUserId) =>
        p.OwnerUserIds is null || p.OwnerUserIds.Contains(ownerUserId);
}
