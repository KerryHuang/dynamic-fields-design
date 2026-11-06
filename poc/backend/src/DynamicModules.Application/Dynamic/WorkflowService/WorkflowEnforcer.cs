using System.Text.Json;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.WorkflowService;

/// <summary>
/// 狀態機的後端強制（前端只是縮小選項）：新增只能從起始狀態開始；修改時狀態有變就必須有 from → to 的定義，
/// 而且使用者在該轉移的群組內（系統管理員可做任何已定義的轉移；沒有定義的轉移任何人都不行）。
/// </summary>
public class WorkflowEnforcer(IPermissionRepository permissions, ICurrentUser user)
{
    public const string NotInitialMessage = "新增時只能使用起始狀態";
    public const string NotAllowedMessage = "不允許這個狀態轉移";
    public const string NotPermittedMessage = "你沒有權限執行這個狀態轉移";
    public const string LockedDetailsMessage = "此狀態的單據不可修改明細";
    public const string LockedDeleteMessage = "此狀態的單據不可刪除";

    /// <summary>新增時沒有帶狀態就補上起始狀態（系統填入，不受欄位唯讀影響）。</summary>
    public static void SeedInitial(Module module, Dictionary<string, JsonElement?> input, HashSet<string> systemKeys)
    {
        if (module.Workflow is not { } wf) return;
        if (input.TryGetValue(wf.StatusFieldKey, out var v) && v is { ValueKind: not JsonValueKind.Null }) return;
        input[wf.StatusFieldKey] = JsonSerializer.SerializeToElement($"opt_{wf.InitialOptionId}");
        systemKeys.Add(wf.StatusFieldKey);
    }

    /// <summary>使用者能做的轉移（給 schema 縮小選項用）：需要模組 Edit 權限；系統管理員全部；其餘依群組。</summary>
    public async Task<IReadOnlyCollection<WorkflowTransition>?> PermittedTransitionsAsync(
        Module module, EffectivePermissions perms, CancellationToken ct)
    {
        if (module.Workflow is not { } wf) return null;
        if (!perms.Can(PermissionAction.Edit)) return [];
        var inputs = await permissions.LoadInputsAsync(user.UserId, module, ct);
        if (inputs is null) return [];
        if (inputs.User.IsSystemAdmin) return wf.Transitions;
        return wf.Transitions.Where(t => t.GroupIds.Count == 0 || inputs.GroupIds.Any(t.GroupIds.Contains)).ToList();
    }

    /// <param name="previous">null 表示新增。</param>
    /// <returns>錯誤（鍵為狀態欄位的 Field.Key）；沒有錯回空字典。</returns>
    public async Task<Dictionary<string, string[]>> CheckAsync(
        Module module, IReadOnlyDictionary<string, JsonElement?>? previous, IReadOnlyDictionary<string, JsonElement?> values, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (module.Workflow is not { } wf) return errors;
        var key = wf.StatusFieldKey;
        var now = wf.StateOf(values);

        if (previous is null)
        {
            if (now != wf.InitialOptionId) errors[key] = [NotInitialMessage];
            return errors;
        }

        // 沒有狀態的既有記錄（狀態機是後來才設定的）視為起始狀態
        var before = wf.StateOf(previous) ?? wf.InitialOptionId;
        if (now == before || (now is null && wf.StateOf(previous) is null)) return errors;
        if (now is not { } to || wf.Find(before, to) is not { } transition)
        {
            errors[key] = [NotAllowedMessage];
            return errors;
        }
        if (transition.GroupIds.Count == 0) return errors;

        var inputs = await permissions.LoadInputsAsync(user.UserId, module, ct);
        var permitted = inputs is not null && (inputs.User.IsSystemAdmin || inputs.GroupIds.Any(transition.GroupIds.Contains));
        if (!permitted) errors[key] = [NotPermittedMessage];
        return errors;
    }
}
