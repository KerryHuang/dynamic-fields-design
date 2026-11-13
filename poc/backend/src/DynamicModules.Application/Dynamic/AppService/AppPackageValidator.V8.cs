using System.Text.RegularExpressions;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Policies;

namespace DynamicModules.Application.Dynamic.AppService;

// 格式 8 的 approvalFlows[]、轉移的 approval、狀態的 editable（簽核設計 §14）。結構層看形狀，數量只用技術上限（PolicyCatalog 的 Max）；
// 系統層生效值由 ApprovalLimitErrors 在預覽、開啟、匯入時另外檢查，模組層覆寫值由 CreateApprovalFlowRequest 在開啟時檢查。
public static partial class AppPackageValidator
{
    /// <summary>檔案格式上限，與 MaxConversions、MaxReports 同性質（計畫可寫死清單）。</summary>
    public const int MaxApprovalFlows = 50;

    /// <summary>套件裡的簽核人來源；users 帳號屬於這個資料庫，不收（§14.2）。</summary>
    private static readonly string[] PackageSources =
        [ApprovalSources.Group, ApprovalSources.Manager, ApprovalSources.UserField, ApprovalSources.InitiatorPick];

    private const string UsersNotInPackage = "套件不收指定使用者（帳號屬於這個資料庫）";
    private const string PackageSourceInvalid = "簽核人來源只能是 group、manager、userField、initiatorPick";
    private const string PackageFallbackInvalid = "備援簽核人只能是群組或主管";

    /// <summary>與 ApprovalDefinitionValidator 的關卡代號規則相同。</summary>
    [GeneratedRegex("^[a-z][a-z0-9-]{0,39}$")]
    private static partial Regex StageCode();

    private static string FlowAt(AppApprovalFlow f, int i) => $"approvalFlows[{f.Code ?? i.ToString()}]";

    // ---- 結構層 ----

    private static void Format8Structure(AppPackageData p, List<string> errors)
    {
        // 轉移的 approval、狀態的 editable 要在「沒有 approvalFlows 就 return」之前檢查，否則沒有流程的舊格式檔案會漏掉
        foreach (var w in p.Workflows.Where(w => w is not null))
        {
            var wat = $"workflows[{w.Module}]";
            foreach (var s in w.States.Where(s => s?.Editable is not null))
                if (p.FormatVersion < 8) errors.Add($"{wat}.states[{s.Option}].editable: formatVersion 8 才支援 editable");
            foreach (var t in w.Transitions.Where(t => t?.Approval is not null))
            {
                var tat = $"{wat}.transitions[{t.From}>{t.To}].approval";
                if (p.FormatVersion < 8) { errors.Add($"{tat}: formatVersion 8 才支援 approval"); continue; }
                if (!t.Approval!.Required) errors.Add($"{tat}.required: 必須是 true（不需簽核就省略 approval）");
                if (t.Approval.NoMatch is not (null or ApprovalNoMatch.Block or ApprovalNoMatch.Direct))
                    errors.Add($"{tat}.noMatch: 必須是 block 或 direct");
            }
        }
        if (p.ApprovalFlows.Count == 0) return;
        // 舊讀取器靜默略過會讓客戶以為單據有簽核卻沒有（§14.1），一律整份拒絕
        if (p.FormatVersion < 8) { errors.Add("approvalFlows: formatVersion 8 才支援 approvalFlows"); return; }
        if (p.ApprovalFlows.Count > MaxApprovalFlows) errors.Add($"approvalFlows: 最多 {MaxApprovalFlows} 個");

        var codes = new HashSet<string>();
        for (var i = 0; i < p.ApprovalFlows.Count; i++)
        {
            var f = p.ApprovalFlows[i];
            if (f is null) { errors.Add($"approvalFlows[{i}]: 不能是 null"); continue; }
            if (!AppId().IsMatch(f.Code ?? "")) errors.Add($"approvalFlows[{i}].code: 格式錯誤（小寫英文、數字、連字號，2 到 40 字，英文開頭）");
            else if (ApprovalFlowEditor.ReservedKeys.Contains(f.Code!)) errors.Add($"approvalFlows[{i}].code: {ApprovalMessages.FlowKeyReserved}");
            else if (!codes.Add(f.Code!)) errors.Add($"approvalFlows[{i}].code: 流程代碼重複 {f.Code}");
            var at = FlowAt(f, i);
            CheckText(f.Name, $"{at}.name", errors);
            if (f.Priority < 0) errors.Add($"{at}.priority: 優先序不能是負數");
            if (f.Shape is null) errors.Add($"{at}.shape: 必填");
            else if (f.Shape != ApprovalShapes.Sequence) errors.Add($"{at}.shape: 格式 8 只支援 sequence");
            if (f.FallbackApprovers is { } fb)
            {
                if (fb.Source is not (ApprovalSources.Group or ApprovalSources.Manager)) errors.Add($"{at}.fallbackApprovers: {PackageFallbackInvalid}");
                else if (SourceStructureError(fb) is { } fe) errors.Add($"{at}.fallbackApprovers: {fe}");
            }

            var stages = f.Stages ?? [];
            if (stages.Count == 0 || stages.Count > PolicyCatalog.ApprovalMaxStages.Max)
                errors.Add($"{at}.stages: {ApprovalMessages.StagesCount(PolicyCatalog.ApprovalMaxStages.Max)}");
            var stageCodes = new HashSet<string>();
            for (var j = 0; j < stages.Count; j++)
            {
                var s = stages[j];
                if (s is null) { errors.Add($"{at}.stages[{j}]: 不能是 null"); continue; }
                var sat = $"{at}.stages[{s.Code ?? j.ToString()}]";
                if (!StageCode().IsMatch(s.Code ?? "")) errors.Add($"{sat}.code: {ApprovalMessages.StageCodeInvalid}");
                else if (!stageCodes.Add(s.Code!)) errors.Add($"{sat}.code: {ApprovalMessages.StageCodeDuplicate}");
                CheckText(s.Name, $"{sat}.name", errors);
                if (s.Mode is not (ApprovalModes.All or ApprovalModes.Any)) errors.Add($"{sat}.mode: {ApprovalMessages.ModeInvalid}");
                if (s.OnEmpty is not (null or ApprovalOnEmpty.Fallback or ApprovalOnEmpty.Skip)) errors.Add($"{sat}.onEmpty: {ApprovalMessages.OnEmptyInvalid}");
                if (s.DueHours is < 1 || s.DueHours > PolicyCatalog.ApprovalMaxDueHours.Max)
                    errors.Add($"{sat}.dueHours: {ApprovalMessages.DueHours(PolicyCatalog.ApprovalMaxDueHours.Max)}");
                if (s.Approvers is null) errors.Add($"{sat}.approvers: {PackageSourceInvalid}");
                else if (SourceStructureError(s.Approvers) is { } se) errors.Add($"{sat}.approvers: {se}");
            }
        }
    }

    /// <summary>來源的形狀，只用技術上限；群組與欄位是否存在交給引用層。</summary>
    private static string? SourceStructureError(AppApproverSource a)
    {
        if (a.Source == ApprovalSources.Users) return UsersNotInPackage;
        if (!PackageSources.Contains(a.Source)) return PackageSourceInvalid;
        switch (a.Source)
        {
            case ApprovalSources.Group:
                return a.Groups is { Count: > 0 } ? null : "群組至少要有一個";
            case ApprovalSources.Manager:
                return a.Level is >= 1 && a.Level <= PolicyCatalog.ApprovalMaxManagerLevel.Max
                    ? null : ApprovalMessages.ManagerLevel(PolicyCatalog.ApprovalMaxManagerLevel.Max);
            case ApprovalSources.UserField:
                if (a.Field is null) return "使用者欄位來源必須指定欄位";
                return a.CandidateGroups is { Count: > 0 } ? null : ApprovalMessages.UserFieldWithoutCandidates;
            default:
                var max = PolicyCatalog.ApprovalMaxApprovers.Max;
                return a.Min is { } min && a.Max is { } pickMax && min >= 1 && min <= pickMax && pickMax <= max ? null : ApprovalMessages.PickRange(max);
        }
    }
}
