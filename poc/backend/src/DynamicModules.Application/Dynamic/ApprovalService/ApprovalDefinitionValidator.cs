using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 流程定義的結構驗證（設計 §4.3、§6.1、§9.2 的定義時限制）；錯誤鍵 shape、when、rejectTo、fallbackApprovers、stages、
/// stages[i].code／name／when／approvers／mode／editableFields／dueHours／onEmpty。跨流程與入口的約束在 ApprovalConstraints。
/// </summary>
public sealed partial class ApprovalDefinitionValidator(
    IPermissionRepository permissions, FieldTypeRegistry registry, IConversionRepository conversions, ILedgerRepository ledgers)
{
    [GeneratedRegex("^[a-z][a-z0-9-]{0,39}$")]
    private static partial Regex CodePattern();

    public async Task<Dictionary<string, string[]>> ValidateAsync(Module module, Workflow wf, ApprovalDefinition d, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        void Err(string key, string message) => errors.TryAdd(key, [message]);

        var users = await permissions.ListUsersAsync(ct);
        var activeUsers = users.Where(u => u.IsActive).Select(u => u.UserId).ToHashSet();
        var allUserIds = users.Select(u => u.UserId).ToHashSet();
        var groupIds = (await permissions.ListGroupsAsync(ct)).Select(g => g.Id).ToHashSet();
        var fields = module.Blocks.SelectMany(b => b.Fields).ToDictionary(f => f.Id);

        if (d.Shape != ApprovalShapes.Sequence) Err("shape", ApprovalMessages.ShapeInvalid);
        if (ConditionError(d.When) is { } we) Err("when", ApprovalMessages.WhenInvalid(we));
        if (d.RejectTo is { } rt && wf.States.All(s => s.OptionId != rt)) Err("rejectTo", ApprovalMessages.RejectToInvalidState);
        if (d.FallbackApprovers is { } fb)
        {
            if (fb.Source is not (ApprovalSources.Users or ApprovalSources.Group or ApprovalSources.Manager)) Err("fallbackApprovers", ApprovalMessages.FallbackSourceInvalid);
            else if (SourceError(fb) is { } fe) Err("fallbackApprovers", fe);
        }

        var stages = d.Stages ?? [];
        if (stages.Count is 0 or > ApprovalLimits.MaxStages) Err("stages", ApprovalMessages.StagesCount);
        else if (stages.Select(s => s.Code).Distinct().Count() != stages.Count) Err("stages", ApprovalMessages.StageCodeDuplicate);

        var nonEditable = await NonEditableFieldIdsAsync(module, wf, ct);
        var hidden = await HiddenFieldIdsAsync(module, ct);
        for (var i = 0; i < stages.Count; i++)
        {
            var s = stages[i];
            var p = $"stages[{i}]";
            if (s.Code is null || !CodePattern().IsMatch(s.Code)) Err($"{p}.code", ApprovalMessages.StageCodeInvalid);
            if (s.Name is null || !s.Name.IsValid(out _)) Err($"{p}.name", ApprovalMessages.StageNameRequired);
            if (ConditionError(s.When) is { } se) Err($"{p}.when", ApprovalMessages.WhenInvalid(se));
            if (s.Approvers is null) Err($"{p}.approvers", ApprovalMessages.SourceInvalid);
            else if (SourceError(s.Approvers) is { } ae) Err($"{p}.approvers", ae);
            if (s.Mode is not (ApprovalModes.All or ApprovalModes.Any)) Err($"{p}.mode", ApprovalMessages.ModeInvalid);
            if (s.DueHours is { } h && h is < 1 or > ApprovalLimits.MaxDueHours) Err($"{p}.dueHours", ApprovalMessages.DueHours);
            if (s.OnEmpty is not (ApprovalOnEmpty.Fallback or ApprovalOnEmpty.Skip)) Err($"{p}.onEmpty", ApprovalMessages.OnEmptyInvalid);
            if ((s.EditableFields ?? []).Any(id => !fields.TryGetValue(id, out var f) || f.Status != FieldStatus.Active || nonEditable.Contains(id)))
                Err($"{p}.editableFields", ApprovalMessages.EditableFieldInvalid);
            else if ((s.EditableFields ?? []).Any(hidden.Contains)) Err($"{p}.editableFields", ApprovalMessages.EditableFieldHidden);
        }
        return errors;

        string? ConditionError(JsonElement? when)
        {
            if (when is not { } w || w.ValueKind == JsonValueKind.Null) return null;
            return ConditionParser.Parse(w, module, registry, ConditionUsage.Rule, allUserIds).Error;
        }

        string? SourceError(ApproverSource a)
        {
            switch (a.Source)
            {
                case ApprovalSources.Users:
                    var ids = a.UserIds ?? [];
                    if (ids.Count is 0 or > ApprovalLimits.MaxApprovers) return ApprovalMessages.UsersCount;
                    return ids.All(activeUsers.Contains) ? null : ApprovalMessages.UsersInvalid;
                case ApprovalSources.Group:
                    return a.GroupIds is { Count: > 0 } g && g.All(groupIds.Contains) ? null : ApprovalMessages.GroupsInvalid;
                case ApprovalSources.Manager:
                    return a.Level is >= 1 and <= ApprovalLimits.MaxManagerLevel ? null : ApprovalMessages.ManagerLevel;
                case ApprovalSources.UserField:
                    if (a.FieldId is not { } fid || !fields.TryGetValue(fid, out var uf) || uf is not { Type: FieldType.User, Status: FieldStatus.Active })
                        return ApprovalMessages.UserFieldInvalid;
                    if (a.CandidateGroupIds is not { Count: > 0 } cg) return ApprovalMessages.UserFieldWithoutCandidates;
                    return cg.All(groupIds.Contains) ? null : ApprovalMessages.CandidateGroupsInvalid;
                case ApprovalSources.InitiatorPick:
                    if (a.Min is not { } min || a.Max is not { } max || min < 1 || min > max || max > ApprovalLimits.MaxApprovers)
                        return ApprovalMessages.PickRange;
                    return (a.CandidateGroupIds ?? []).All(groupIds.Contains) ? null : ApprovalMessages.CandidateGroupsInvalid;
                default:
                    return ApprovalMessages.SourceInvalid;
            }
        }
    }

    /// <summary>
    /// 對某些群組看不到的欄位：任何群組設為 Hidden、或限定可見（共用主檔貢獻欄位）。存流程時還不知道最後由誰簽（主管、備援、自選），
    /// 所以只要有群組看不到就不能當可編輯欄位（裁決 22，fail closed）；執行期另在同意時依簽核人本人的權限再擋一次。
    /// </summary>
    private async Task<HashSet<long>> HiddenFieldIdsAsync(Module module, CancellationToken ct)
    {
        var ids = (await permissions.LoadModuleRowsAsync(module, ct)).FieldPermissions.Where(p => p.Access == FieldAccess.Hidden)
            .Select(p => p.FieldId).ToHashSet();
        ids.UnionWith(module.Blocks.SelectMany(b => b.Fields).Where(f => f.VisibilityRestricted).Select(f => f.Id));
        return ids;
    }

    /// <summary>不能當可編輯欄位的欄位：計算欄位、狀態欄位、轉換追溯欄位、分錄沖銷欄位、lock 帶值的目的欄位。</summary>
    private async Task<HashSet<long>> NonEditableFieldIdsAsync(Module module, Workflow wf, CancellationToken ct)
    {
        var all = module.Blocks.SelectMany(b => b.Fields).ToList();
        var ids = all.Where(f => f.Type is FieldType.Formula or FieldType.Rollup or FieldType.AutoNumber).Select(f => f.Id).ToHashSet();
        ids.Add(wf.StatusFieldId);
        foreach (var f in all.Where(f => f.Config.Fill is { Mode: FillConfig.Lock }))
            ids.UnionWith(f.Config.Fill!.Map.Select(m => m.To));
        foreach (var c in await conversions.ListTouchingAsync(module.Id, ct))
            if (c.TargetModuleId == module.Id) ids.Add(c.Definition.Header.SourceFieldId);
        if (await ledgers.GetByModuleAsync(module.Id, ct) is { } ledger) ids.Add(ledger.Definition.ReversalFieldId);
        return ids;
    }
}
