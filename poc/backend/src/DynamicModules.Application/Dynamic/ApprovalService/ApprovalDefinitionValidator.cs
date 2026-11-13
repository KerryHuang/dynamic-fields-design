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
    IPermissionRepository permissions, FieldTypeRegistry registry, ApprovalEditableFields editableFields,
    ISharedMasterRepository shared, ApprovalPolicies approvalPolicies)
{
    [GeneratedRegex("^[a-z][a-z0-9-]{0,39}$")]
    private static partial Regex CodePattern();

    /// <summary>previous 是這個流程目前版本的定義（新流程為 null）：數值上限「改嚴只管之後」，超過上限而且比舊版大才擋（政策設計 §3.5）。</summary>
    public async Task<Dictionary<string, string[]>> ValidateAsync(
        Module module, Workflow wf, ApprovalDefinition d, ApprovalDefinition? previous, CancellationToken ct)
    {
        var limits = await approvalPolicies.LimitsAsync(module.Id, ct);
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
            else if (SourceError(fb, previous?.FallbackApprovers) is { } fe) Err("fallbackApprovers", fe);
        }

        var stages = d.Stages ?? [];
        if (stages.Count == 0 || (stages.Count > limits.MaxStages && stages.Count > (previous?.Stages?.Count ?? 0)))
            Err("stages", ApprovalMessages.StagesCount(limits.MaxStages, previous is not null));
        else if (stages.Select(s => s.Code).Distinct().Count() != stages.Count) Err("stages", ApprovalMessages.StageCodeDuplicate);

        var nonEditable = await editableFields.NonEditableAsync(module, wf, ct);
        var hiddenRows = (await permissions.LoadModuleRowsAsync(module, ct)).FieldPermissions.Where(p => p.Access == FieldAccess.Hidden).ToList();
        var restricted = fields.Values.Where(f => f.VisibilityRestricted).ToList();
        var grants = restricted.Count == 0 ? [] : (await shared.ListFieldGrantsAsync(restricted.Select(f => f.Id).ToList(), ct))
            .GroupBy(g => g.FieldId).ToDictionary(g => g.Key, g => g.Select(x => x.GroupId).ToHashSet());
        var groupsOf = (await permissions.ListMembershipsAsync(ct)).ToLookup(m => m.UserId, m => m.GroupId);
        for (var i = 0; i < stages.Count; i++)
        {
            var s = stages[i];
            var p = $"stages[{i}]";
            var before = s.Code is null ? null : previous?.Stages?.FirstOrDefault(x => x.Code == s.Code);
            if (s.Code is null || !CodePattern().IsMatch(s.Code)) Err($"{p}.code", ApprovalMessages.StageCodeInvalid);
            if (s.Name is null || !s.Name.IsValid(out _)) Err($"{p}.name", ApprovalMessages.StageNameRequired);
            if (ConditionError(s.When) is { } se) Err($"{p}.when", ApprovalMessages.WhenInvalid(se));
            if (s.Approvers is null) Err($"{p}.approvers", ApprovalMessages.SourceInvalid);
            else if (SourceError(s.Approvers, before?.Approvers) is { } ae) Err($"{p}.approvers", ae);
            if (s.Mode is not (ApprovalModes.All or ApprovalModes.Any)) Err($"{p}.mode", ApprovalMessages.ModeInvalid);
            if (s.DueHours is { } h && (h < 1 || (h > limits.MaxDueHours && h > (before?.DueHours ?? 0))))
                Err($"{p}.dueHours", ApprovalMessages.DueHours(limits.MaxDueHours, previous is not null));
            if (s.OnEmpty is not (ApprovalOnEmpty.Fallback or ApprovalOnEmpty.Skip)) Err($"{p}.onEmpty", ApprovalMessages.OnEmptyInvalid);
            else if (s.OnEmpty == ApprovalOnEmpty.Skip && s.Approvers is { } src && (src.CandidatesUnmatched || src.UnmatchedGroups is { Count: > 0 }))
                Err($"{p}.onEmpty", ApprovalMessages.OnEmptySkipUnmatched);
            if ((s.EditableFields ?? []).Any(id => !fields.TryGetValue(id, out var f) || f.Status != FieldStatus.Active || nonEditable.Contains(id)))
                Err($"{p}.editableFields", ApprovalMessages.EditableFieldInvalid);
            else if (s.Approvers is not null && (s.EditableFields ?? []).Count > 0
                     && (s.EditableFields ?? []).Any(HiddenForDerivable(s, d.FallbackApprovers, hiddenRows, grants, groupsOf, restricted).Contains)) Err($"{p}.editableFields", ApprovalMessages.EditableFieldHidden);
        }
        return errors;

        string? ConditionError(JsonElement? when)
        {
            if (when is not { } w || w.ValueKind == JsonValueKind.Null) return null;
            return ConditionParser.Parse(w, module, registry, ConditionUsage.Rule, allUserIds).Error;
        }

        string? SourceError(ApproverSource a, ApproverSource? old)
        {
            switch (a.Source)
            {
                case ApprovalSources.Users:
                    var ids = a.UserIds ?? [];
                    var oldCount = old?.Source == ApprovalSources.Users ? old.UserIds?.Count ?? 0 : 0;
                    if (ids.Count == 0 || (ids.Count > limits.MaxApprovers && ids.Count > oldCount)) return ApprovalMessages.UsersCount(limits.MaxApprovers, previous is not null);
                    return ids.All(activeUsers.Contains) ? null : ApprovalMessages.UsersInvalid;
                case ApprovalSources.Group:
                    // 開啟套件時略過的群組記成對不上的單位（§14.4）：全部被略過時 GroupIds 為空，仍算有來源，執行時整關交給備援
                    var g = a.GroupIds ?? [];
                    return g.Count + (a.UnmatchedGroups?.Count ?? 0) > 0 && g.All(groupIds.Contains) ? null : ApprovalMessages.GroupsInvalid;
                case ApprovalSources.Manager:
                    var oldLevel = old?.Source == ApprovalSources.Manager ? old.Level ?? 0 : 0;
                    return a.Level is { } lv && lv >= 1 && (lv <= limits.MaxManagerLevel || lv <= oldLevel) ? null : ApprovalMessages.ManagerLevel(limits.MaxManagerLevel, previous is not null);
                case ApprovalSources.UserField:
                    if (a.FieldId is not { } fid || !fields.TryGetValue(fid, out var uf) || uf is not { Type: FieldType.User, Status: FieldStatus.Active })
                        return ApprovalMessages.UserFieldInvalid;
                    // 候選群組在開啟時被略過（CandidatesUnmatched）：執行時不看候選、一律交給備援，所以允許候選清單為空（§14.4）
                    var cg = a.CandidateGroupIds ?? [];
                    if (cg.Count == 0 && !a.CandidatesUnmatched) return ApprovalMessages.UserFieldWithoutCandidates;
                    return cg.All(groupIds.Contains) ? null : ApprovalMessages.CandidateGroupsInvalid;
                case ApprovalSources.InitiatorPick:
                    var oldMax = old?.Source == ApprovalSources.InitiatorPick ? old.Max ?? 0 : 0;
                    if (a.Min is not { } min || a.Max is not { } max || min < 1 || min > max || (max > limits.MaxApprovers && max > oldMax))
                        return ApprovalMessages.PickRange(limits.MaxApprovers, previous is not null);
                    // 原本候選群組被開啟套件略過（空候選 + 標記）：只清標記會變成「任何人都可被選」，要先選候選群組。沒被略過過的來源，空候選仍是「不限」（§6.3）
                    if (old is { Source: ApprovalSources.InitiatorPick, CandidatesUnmatched: true } && !a.CandidatesUnmatched && (a.CandidateGroupIds ?? []).Count == 0)
                        return ApprovalMessages.PickCandidatesUnmatchedRemoved;
                    return (a.CandidateGroupIds ?? []).All(groupIds.Contains) ? null : ApprovalMessages.CandidateGroupsInvalid;
                default:
                    return ApprovalMessages.SourceInvalid;
            }
        }
    }

    /// <summary>
    /// 這一關存檔時推得出的簽核人看不到的欄位（裁決 22，主控修正輪 1）。推得出的群組集合：來源是群組時每個群組各一組；
    /// 來源是指定使用者時每位使用者所屬的群組各一組；流程的備援也照這兩條算。一組群組看不到 = 其中有群組設為 Hidden，
    /// 或欄位限定可見而這組群組都沒有可見列（與 PermissionResolver 相同）。主管、使用者欄位、發起人自選推不出人選，不在存檔時擋，
    /// 靠執行期同意時依簽核人本人的權限回 400。
    /// </summary>
    private static HashSet<long> HiddenForDerivable(
        ApprovalStageDef stage, ApproverSource? fallback, List<FieldPermission> hiddenRows, Dictionary<long, HashSet<long>> grants,
        ILookup<string, long> groupsOf, IReadOnlyList<Field> restricted)
    {
        var ids = new HashSet<long>();
        foreach (var source in new[] { stage.Approvers, fallback })
        {
            IEnumerable<HashSet<long>> sets = source?.Source switch
            {
                ApprovalSources.Group => (source.GroupIds ?? []).Select(g => new HashSet<long> { g }),
                ApprovalSources.Users => (source.UserIds ?? []).Select(u => groupsOf[u].ToHashSet()),
                _ => []
            };
            foreach (var set in sets)
            {
                ids.UnionWith(hiddenRows.Where(p => set.Contains(p.GroupId)).Select(p => p.FieldId));
                ids.UnionWith(restricted.Where(f => !grants.TryGetValue(f.Id, out var gs) || !gs.Overlaps(set)).Select(f => f.Id));
            }
        }
        return ids;
    }
}
