using System.Text.Json;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.AppService;

/// <summary>
/// 簽核流程在套件（代號）與資料庫（Id）之間互轉（簽核設計 §14）。開啟時流程一律保留：被略過的群組不從名單移除，
/// 而是記成對不上的來源單位，由備援簽核人頂替（§14.4）；<see cref="Substitutions"/> 列出每一個被頂替的來源，預覽與開啟共用。
/// 欄位或選項解析不到時累積成錯誤、回 null，不猜。
/// </summary>
public static class AppApprovalFlowCodec
{
    public const string KindApprovers = "approvers", KindCandidates = "candidates", KindFallback = "fallback";
    public const string ByFlowFallback = "flowFallback", ByPolicyDefault = "policyDefault";

    /// <summary>開啟套件時被略過的群組造成的頂替。Kind：approvers（關卡來源的群組）、candidates（候選群組）、fallback（備援群組）。
    /// ReplacedBy：flowFallback（流程的備援簽核人）或 policyDefault（政策 approval.defaultFallbackApprovers）。Stage 在 Kind = fallback 時為 null。</summary>
    public sealed record Substitution(string Flow, I18nText FlowName, string? Stage, I18nText? StageName,
        string Group, I18nText GroupName, string Kind, string ReplacedBy);

    // ---- 匯入方向：代號 → Id ----

    /// <param name="groupIds">對應到的群組代號 → Id；不在裡面的就是被略過的。</param>
    /// <param name="groupName">套件群組代號 → 顯示名稱（依開啟的 locale），寫進 UnmatchedGroups。</param>
    public static (ApprovalFlowInput? Input, List<string> Errors) ToInput(
        AppApprovalFlow f, AppReportCodec.Lookups l, IReadOnlyDictionary<string, long> groupIds, Func<string, string> groupName)
    {
        var errors = new List<string>();
        long Option(string code)
        {
            var id = l.OptionId($"{f.Module}.{code}");
            if (id is null) errors.Add($"引用了套件沒有的狀態 {code}（模組 {f.Module}）");
            return id ?? 0;
        }
        long Field(string? name)
        {
            var id = name is null ? null : l.FieldId(name);
            if (id is null) errors.Add($"引用了套件沒有的欄位 {name}");
            return id ?? 0;
        }
        JsonElement? When(JsonElement? json) =>
            json is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } w ? AppEffectsCodec.ImportCondition(w, l, errors) : null;
        List<long> Mapped(IEnumerable<string>? codes) => (codes ?? []).Where(groupIds.ContainsKey).Select(c => groupIds[c]).Distinct().ToList();
        bool AnySkipped(IEnumerable<string>? codes) => (codes ?? []).Any(c => !groupIds.ContainsKey(c));

        ApproverSource Source(AppApproverSource a)
        {
            var source = new ApproverSource(a.Source, GroupIds: a.Source == ApprovalSources.Group ? Mapped(a.Groups) : null, Level: a.Level,
                FieldId: a.Source == ApprovalSources.UserField ? Field(a.Field) : null,
                CandidateGroupIds: a.Source is ApprovalSources.UserField or ApprovalSources.InitiatorPick ? Mapped(a.CandidateGroups) : null,
                Min: a.Min, Max: a.Max);
            var unmatched = a.Source == ApprovalSources.Group
                ? (a.Groups ?? []).Where(c => !groupIds.ContainsKey(c)).Distinct().Select(groupName).ToList()
                : [];
            // 候選群組任何一個被略過，整個來源視為對不上（§14.4 字面，主控裁決 U5）
            return source with
            {
                UnmatchedGroups = unmatched.Count > 0 ? unmatched : null,
                CandidatesUnmatched = a.Source is ApprovalSources.UserField or ApprovalSources.InitiatorPick && AnySkipped(a.CandidateGroups)
            };
        }

        var stages = (f.Stages ?? []).Select(s =>
        {
            var approvers = Source(s.Approvers);
            var unmatched = approvers.CandidatesUnmatched || approvers.UnmatchedGroups is { Count: > 0 };
            // 略過群組不能變成少簽：onEmpty = skip 改成 fallback（§14.4）
            var onEmpty = s.OnEmpty is null || (unmatched && s.OnEmpty == ApprovalOnEmpty.Skip) ? ApprovalOnEmpty.Fallback : s.OnEmpty;
            return new ApprovalStageDef(s.Code, s.Name, When(s.When), approvers, s.Mode,
                (s.EditableFields ?? []).Select(Field).ToList(), s.DueHours, onEmpty);
        }).ToList();
        // 備援群組任何一個被略過：整份備援改用政策預設（§14.4）
        var fallback = f.FallbackApprovers is { } fb && !AnySkipped(fb.Groups) ? Source(fb) : null;
        var definition = new ApprovalDefinition(f.Shape, When(f.When), f.RejectTo is { } rt ? Option(rt) : null, fallback, stages);
        var input = new ApprovalFlowInput(f.Code, f.Name, Option(f.To), f.Priority, f.Active, definition);
        return errors.Count == 0 ? (input, []) : (null, errors.Distinct().ToList());
    }

    /// <summary>
    /// 被略過的群組造成的頂替，每個（關卡、群組、種類）一筆；預覽以 groupMap 的 map／create 為已對應，開啟以實際對應到 Id 的群組為已對應，
    /// 同一個函式保證預覽說的就是開啟做的。
    /// </summary>
    public static IReadOnlyList<Substitution> Substitutions(AppApprovalFlow f, IReadOnlySet<string> mappedGroupCodes, IReadOnlyList<AppGroup> groups)
    {
        var result = new List<Substitution>();
        I18nText Name(string code) => groups.FirstOrDefault(g => g.Code == code)?.Name ?? new I18nText([KeyValuePair.Create(I18nText.FallbackLocale, code)]);
        IEnumerable<string> Skipped(IEnumerable<string>? codes) => (codes ?? []).Where(c => !mappedGroupCodes.Contains(c)).Distinct();

        var fallbackSkipped = f.FallbackApprovers is { } fb && Skipped(fb.Groups).Any();
        var by = f.FallbackApprovers is not null && !fallbackSkipped ? ByFlowFallback : ByPolicyDefault;
        foreach (var s in (f.Stages ?? []).Where(s => s?.Approvers is not null))
        {
            var a = s.Approvers;
            if (a.Source == ApprovalSources.Group)
                result.AddRange(Skipped(a.Groups).Select(g => new Substitution(f.Code, f.Name, s.Code, s.Name, g, Name(g), KindApprovers, by)));
            if (a.Source is ApprovalSources.UserField or ApprovalSources.InitiatorPick)
                result.AddRange(Skipped(a.CandidateGroups).Select(g => new Substitution(f.Code, f.Name, s.Code, s.Name, g, Name(g), KindCandidates, by)));
        }
        if (fallbackSkipped)
            result.AddRange(Skipped(f.FallbackApprovers!.Groups).Select(g => new Substitution(f.Code, f.Name, null, null, g, Name(g), KindFallback, ByPolicyDefault)));
        return result;
    }
}
