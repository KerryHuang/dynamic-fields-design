using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.WorkflowService;

/// <summary>
/// 狀態機的後端強制（前端只是縮小選項）：新增只能從起始狀態開始；修改時狀態有變就必須有 from → to 的定義，
/// 而且使用者在該轉移的群組內（系統管理員可做任何已定義的轉移；沒有定義的轉移任何人都不行）。
/// </summary>
public class WorkflowEnforcer(IPermissionRepository permissions, ICurrentUser user, ISystemIdentity system, ICurrentLocale locale)
{
    // 繁中文字取自字串表；簽核（ApprovalService）與既有測試還以字串用這兩句，之後簽核語系化時改走鍵
    public static readonly string NotAllowedMessage = MessageText.Get(null, MessageText.WorkflowNotAllowed);
    public static readonly string NotPermittedMessage = MessageText.Get(null, MessageText.WorkflowNotPermitted);

    private static bool HasState(JsonElement? v) =>
        v is { ValueKind: not JsonValueKind.Null } s && !(s.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(s.GetString()));

    /// <summary>新增時沒有帶狀態（沒送、null、空字串）就補上起始狀態（系統填入，不受欄位唯讀影響）。</summary>
    public static void SeedInitial(Module module, Dictionary<string, JsonElement?> input, HashSet<string> systemKeys)
    {
        if (module.Workflow is not { } wf) return;
        if (input.TryGetValue(wf.StatusFieldKey, out var v) && HasState(v)) return;
        Seed(wf, input, systemKeys);
    }

    /// <summary>
    /// 修改時，狀態機是後來才設定的既有記錄（沒有狀態）在這次存檔補上起始狀態，不留沒有狀態的記錄；
    /// 這次請求自己帶了狀態就照請求的處理（之後由 <see cref="CheckAsync"/> 判斷轉移）。
    /// </summary>
    public static void SeedMissingState(
        Module module, IReadOnlyDictionary<string, JsonElement?> previous, Dictionary<string, JsonElement?> input, HashSet<string> systemKeys)
    {
        if (module.Workflow is not { } wf || wf.StateOf(previous) is not null) return;
        if (input.TryGetValue(wf.StatusFieldKey, out var v) && HasState(v)) return;
        Seed(wf, input, systemKeys);
    }

    private static void Seed(Workflow wf, Dictionary<string, JsonElement?> input, HashSet<string> systemKeys)
    {
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
            if (now != wf.InitialOptionId) errors[key] = [MessageText.Get(locale.Locale, MessageText.WorkflowNotInitial)];
            return errors;
        }

        // 沒有狀態的既有記錄（狀態機是後來才設定的）視為起始狀態
        var before = wf.StateOf(previous) ?? wf.InitialOptionId;
        if (now == before || (now is null && wf.StateOf(previous) is null)) return errors;
        if (now is not { } to || wf.Find(before, to) is not { } transition)
        {
            errors[key] = [MessageText.Get(locale.Locale, MessageText.WorkflowNotAllowed)];
            return errors;
        }
        // 需簽核的轉移只能走送出端點（簽核設計 §5.3）；系統身分（事件、簽核完成的轉移）交給簽核後盾判斷
        if (!system.Active && transition.Approval is { Required: true })
        {
            errors[key] = [ApprovalService.ApprovalMessages.NeedsSubmit];
            return errors;
        }
        if (transition.GroupIds.Count == 0 || system.Active) return errors;   // 事件效果的轉移必須是已定義的，但不檢查群組

        var inputs = await permissions.LoadInputsAsync(user.UserId, module, ct);
        var permitted = inputs is not null && (inputs.User.IsSystemAdmin || inputs.GroupIds.Any(transition.GroupIds.Contains));
        if (!permitted) errors[key] = [MessageText.Get(locale.Locale, MessageText.WorkflowNotPermitted)];
        return errors;
    }
}
