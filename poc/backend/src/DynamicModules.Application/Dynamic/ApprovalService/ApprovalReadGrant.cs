using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 簽核人的臨時讀取權（設計 §10.1）：使用者是這筆記錄某個 open 任務的可動作者時，對這一筆記錄唯讀，另給 Pricing。
/// 欄位可見性照使用者自己群組的欄位例外（限制優先）；只給單筆讀取與附件下載用，清單、匯出、反向關聯不經過這裡。
/// 查詢失敗或資料缺漏一律不放行。
/// </summary>
public sealed class ApprovalReadGrant(
    IApprovalInstanceRepository repo, IPermissionRepository permissions, ICurrentUser user, FieldLocks locks, ApprovalHiddenFields hiddenFields)
{
    private static readonly IReadOnlySet<PermissionAction> GrantedActions = new HashSet<PermissionAction> { PermissionAction.Read, PermissionAction.Pricing };

    /// <summary>是可動作者時回傳表頭的權限：自己的欄位例外＋Actions = {Read, Pricing}、Scope = All、OwnerUserIds = null；否則 null。</summary>
    public async Task<EffectivePermissions?> ForRecordAsync(Module module, Record record, CancellationToken ct)
    {
        try
        {
            if (!await repo.IsOpenActorAsync(record.Id, user.UserId, ct)) return null;
            var inputs = await permissions.LoadInputsAsync(user.UserId, module, ct);
            if (inputs is null) return null;
            var elevated = Elevate(inputs, module);
            var (ok, hiddenSource) = await hiddenFields.SourceAsync(module, elevated, user.UserId, ct);
            if (!ok) return null;
            return await locks.ApplyAsync(module, Grant(PermissionResolver.Resolve(module, elevated, null, hiddenSource, PermissionAction.Read)), ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;   // 查詢失敗不放行（設計 §10.1）
        }
    }

    /// <summary>同一筆記錄的明細：動作軸取表頭（已確認是可動作者），欄位例外取明細模組。呼叫端只在 ForRecordAsync 放行之後才用。</summary>
    public async Task<EffectivePermissions?> ForDetailAsync(Module parent, Module child, CancellationToken ct)
    {
        var parentInputs = await permissions.LoadInputsAsync(user.UserId, parent, ct);
        var childInputs = await permissions.LoadInputsAsync(user.UserId, child, ct);
        if (parentInputs is null || childInputs is null) return null;
        var effective = PermissionResolver.Resolve(child, ApprovalHiddenFields.DetailInputs(Elevate(parentInputs, parent), childInputs), parent,
            forAction: PermissionAction.Read);
        return await locks.ApplyAsync(child, Grant(effective), ct);
    }

    /// <summary>個人權限換成「讀取＋Pricing」：Pricing 判斷照常走 PermissionResolver，欄位例外不受影響。</summary>
    private static PermissionInputs Elevate(PermissionInputs inputs, Module access) => inputs with
    {
        UserPrivilege = new UserProgramPrivilege { UserId = inputs.User.UserId, ProgramCode = access.ProgramCode, CanRead = true, CanAmt = true }
    };

    private static EffectivePermissions Grant(EffectivePermissions p) =>
        p with { Actions = GrantedActions, Scope = DataScope.All, OwnerUserIds = null, Hash = p.Hash + "-ag" };
}
