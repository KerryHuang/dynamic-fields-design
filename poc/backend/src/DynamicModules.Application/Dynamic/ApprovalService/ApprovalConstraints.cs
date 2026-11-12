using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 設計 §5.2「重算的約束」：儲存狀態機、儲存流程、改流程目標狀態時，對每個目標狀態上的每個流程、每個入口全部重算。
/// 錯誤鍵 transitions[{from}>{to}]、states[{optionId}]、initialOptionId、flows[{key}]；同一個鍵可以有多則訊息。
/// </summary>
public static class ApprovalConstraints
{
    private const string Locale = "zh-TW";

    public static Dictionary<string, string[]> Validate(Module module, Workflow wf, IReadOnlyList<FlowState> flows)
    {
        var errors = new Dictionary<string, List<string>>();
        void Err(string key, string message)
        {
            if (!errors.TryGetValue(key, out var list)) errors[key] = list = [];
            if (!list.Contains(message)) list.Add(message);
        }

        var fields = module.Blocks.SelectMany(b => b.Fields).ToDictionary(f => f.Id);
        var options = fields.TryGetValue(wf.StatusFieldId, out var status)
            ? status.Options.ToDictionary(o => o.Id, o => o.LabelI18n.Resolve(Locale))
            : [];
        string S(long id) => options.GetValueOrDefault(id) ?? id.ToString();
        string F(long id) => fields.TryGetValue(id, out var f) ? f.LabelI18n.Resolve(Locale) : id.ToString();
        static string T(long from, long to) => $"transitions[{from}>{to}]";

        var targets = ApprovalTopology.ApprovalTargets(wf);
        var locked = ApprovalTopology.LockedStates(wf);
        var editableFlags = wf.States.Where(s => s.Editable).Select(s => s.OptionId).ToHashSet();
        var readOnly = wf.States.Where(s => s.ReadOnly).Select(s => s.OptionId).ToHashSet();

        foreach (var to in targets.Order())
        {
            var entries = wf.EntriesOf(to).ToList();
            foreach (var t in entries.Where(t => t.Approval is not { Required: true }))
                Err(T(t.From, t.To), ApprovalMessages.EntryMismatch(S(to)));

            var onTarget = flows.Where(f => f.Flow.ToOptionId == to).ToList();
            if (onTarget.Count == 0) Err($"states[{to}]", ApprovalMessages.NoFlow(S(to)));
            else if (entries.Any(t => t.Approval is { Required: true, NoMatch: ApprovalNoMatch.Direct }) && !onTarget.Any(f => f.Flow.Active))
                Err($"states[{to}]", ApprovalMessages.DirectNeedsActive(S(to)));

            if (editableFlags.Contains(to)) Err($"states[{to}]", ApprovalMessages.LockedMarkedEditable(S(to)));

            var approvalEntries = entries.Where(t => t.Approval is { Required: true }).ToList();
            foreach (var fs in onTarget)
            {
                var key = $"flows[{fs.Flow.Key}]";
                var name = fs.Flow.NameI18n.Resolve(Locale);
                if (fs.Definition.RejectTo is { } rt)
                    foreach (var e in approvalEntries.Where(e => e.From != rt))
                    {
                        var back = wf.Find(e.From, rt);
                        if (back is null || back.Approval is { Required: true }) Err(key, ApprovalMessages.RejectToInvalid(name, S(e.From), S(rt)));
                        if (locked.Contains(rt) && !locked.Contains(e.From)) Err(key, ApprovalMessages.RejectToLocked(name));
                    }
                var hasEditable = (fs.Definition.Stages ?? []).Any(s => (s.EditableFields ?? []).Count > 0);
                if (hasEditable && approvalEntries.Any(e => readOnly.Contains(e.From) || locked.Contains(e.From)))
                    Err(key, ApprovalMessages.EditableFromLocked(name));
            }

            foreach (var v in ClosureViolationsOf(module, wf, onTarget, F)) Err(v.Key, v.Message);
        }

        // 進入鎖定狀態一律要簽（§5.2）：從非鎖定狀態進入任何鎖定狀態的轉移都必須需簽核。
        foreach (var t in wf.Transitions.Where(t => t.Approval is not { Required: true } && locked.Contains(t.To) && !locked.Contains(t.From)))
            Err(T(t.From, t.To), ApprovalMessages.LockEntry(S(t.From), S(t.To)));

        if (locked.Contains(wf.InitialOptionId)) Err("initialOptionId", ApprovalMessages.InitialLocked);

        return errors.ToDictionary(e => e.Key, e => e.Value.ToArray());
    }

    /// <summary>一則「可編輯欄位落在條件閉包」違規：以（流程鍵、可編輯欄位 Id）識別，訊息另附經過的路徑，路徑改變不算新違規。</summary>
    public readonly record struct ClosureViolation(string FlowKey, long FieldId, string Message)
    {
        public string Key => $"flows[{FlowKey}]";
    }

    /// <summary>只算「可編輯欄位不落在條件或簽核人來源上游閉包」這一項（§9.2），供改欄位設定後重檢新增的違規。</summary>
    public static IReadOnlyList<ClosureViolation> ClosureViolations(Module module, Workflow wf, IReadOnlyList<FlowState> flows)
    {
        var fields = module.Blocks.SelectMany(b => b.Fields).ToDictionary(f => f.Id);
        string F(long id) => fields.TryGetValue(id, out var f) ? f.LabelI18n.Resolve(Locale) : id.ToString();
        return [.. ApprovalTopology.ApprovalTargets(wf).Order()
            .SelectMany(to => ClosureViolationsOf(module, wf, flows.Where(f => f.Flow.ToOptionId == to).ToList(), F))];
    }

    /// <summary>可編輯欄位不能落在同一目標狀態上任何流程的條件或簽核人來源的上游閉包裡（§9.2）。</summary>
    private static IEnumerable<ClosureViolation> ClosureViolationsOf(Module module, Workflow wf, List<FlowState> onTarget, Func<long, string> F)
    {
        var seedOwner = new Dictionary<long, FlowState>();
        foreach (var fs in onTarget)
            foreach (var id in FieldClosure.DirectFields(fs.Definition))
                seedOwner.TryAdd(id, fs);
        var closure = FieldClosure.Upstream(module, seedOwner.Keys, wf.StatusFieldId);
        foreach (var fs in onTarget)
            foreach (var id in (fs.Definition.Stages ?? []).SelectMany(s => s.EditableFields ?? []).Distinct())
            {
                if (!closure.Paths.TryGetValue(id, out var path)) continue;
                var owner = seedOwner[path[0]];
                var via = path.Count > 1 ? string.Join(" → ", path.Take(path.Count - 1).Reverse().Select(p => $"「{F(p)}」")) : "條件直接引用";
                yield return new ClosureViolation(fs.Flow.Key, id, ApprovalMessages.EditableInClosure(F(id), via, owner.Flow.NameI18n.Resolve(Locale)));
            }
    }
}
