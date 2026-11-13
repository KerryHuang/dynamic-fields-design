using System.Text.RegularExpressions;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.ValueObjects;
using static DynamicModules.Application.Dynamic.TemplateService.TemplateGraph;

namespace DynamicModules.Application.Dynamic.AppService;

// 格式 8 的 approvalFlows[]、轉移的 approval、狀態的 editable（簽核設計 §14）。結構層看形狀，數量只用技術上限（PolicyCatalog 的 Max）；
// 系統層生效值由 ApprovalLimitErrors 在預覽、開啟、匯入時另外檢查，模組層覆寫值由 CreateApprovalFlowRequest 在開啟時檢查。
// 引用層確認模組、狀態、欄位、群組都在套件內，狀態一致性重用 ApprovalConstraints（以暫時 Id 組出狀態機，錯誤換回代號）；
// 可編輯欄位的公式閉包（§9.2）與事件不推動需簽核轉移（§5.3）要解析公式與事件，留給開啟時的設計器指令（失敗整批回滾）。
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

    /// <summary>
    /// 系統層生效值（呼叫端讀政策後傳入；靜態的 Validate 在啟動時也會跑，那時不能讀資料庫），預覽、開啟、匯入共用，同 TaskRuleLimitErrors。
    /// 模組層覆寫值由 CreateApprovalFlowRequest 在開啟時依模組再驗一次。null 流程與關卡略過，結構層已報錯。
    /// </summary>
    public static IReadOnlyList<string> ApprovalLimitErrors(AppPackageData p, ApprovalLimitSet limits)
    {
        var errors = new List<string>();
        void Source(AppApproverSource? a, string at)
        {
            if (a is { Source: ApprovalSources.Manager, Level: { } level } && level > limits.MaxManagerLevel)
                errors.Add($"{at}: {ApprovalMessages.ManagerLevel(limits.MaxManagerLevel)}");
            if (a is { Source: ApprovalSources.InitiatorPick, Max: { } max } && max > limits.MaxApprovers)
                errors.Add($"{at}: {ApprovalMessages.PickRange(limits.MaxApprovers)}");
        }
        for (var i = 0; i < p.ApprovalFlows.Count; i++)
        {
            if (p.ApprovalFlows[i] is not { } f) continue;
            var at = FlowAt(f, i);
            var stages = f.Stages ?? [];
            if (stages.Count > limits.MaxStages) errors.Add($"{at}.stages: {ApprovalMessages.StagesCount(limits.MaxStages)}");
            Source(f.FallbackApprovers, $"{at}.fallbackApprovers");
            for (var j = 0; j < stages.Count; j++)
            {
                if (stages[j] is not { } s) continue;
                var sat = $"{at}.stages[{s.Code ?? j.ToString()}]";
                if (s.DueHours > limits.MaxDueHours) errors.Add($"{sat}.dueHours: {ApprovalMessages.DueHours(limits.MaxDueHours)}");
                Source(s.Approvers, $"{sat}.approvers");
            }
        }
        return errors;
    }

    // ---- 引用層 ----

    private static void ApprovalFlowReferences(
        AppPackageData p, Dictionary<string, TemplateField> fields, HashSet<string> groupCodes, List<string> errors, List<string> warnings)
    {
        if (p.ApprovalFlows.Count == 0 && p.Workflows.All(w => w?.Transitions.All(t => t?.Approval is null) != false)) return;
        var modules = p.Modules.Where(m => m is not null).GroupBy(m => m.Code).ToDictionary(g => g.Key, g => g.First());
        var workflows = p.Workflows.Where(w => w?.Module is not null).GroupBy(w => w.Module).ToDictionary(g => g.Key, g => g.First());
        var optionNames = p.Modules.Where(m => m is not null)
            .SelectMany(m => m.Blocks.SelectMany(b => b.Fields).SelectMany(f => f.Options.Select(o => $"{m.Code}.{o.Code}"))).ToHashSet();
        var priorities = new HashSet<(string, string, int)>();
        var valid = new List<AppApprovalFlow>();
        // 有流程沒通過引用檢查的模組 → 那些流程的目標狀態：一致性檢查少了那個流程，這些狀態的「沒有流程」「沒有啟用中的流程」會是誤報（審查 N3）。
        // 目標本身無效（null 代表全部）時看不出原本要掛在哪個狀態，整個模組都略過；同模組其他狀態真的缺流程照報（複審 N-a）
        var incomplete = new Dictionary<string, HashSet<string>?>();
        void Incomplete(string module, string? to, bool targetKnown)
        {
            if (!targetKnown || to is null) incomplete[module] = null;
            else if (!incomplete.TryGetValue(module, out var set)) incomplete[module] = [to];
            else set?.Add(to);
        }

        for (var i = 0; i < p.ApprovalFlows.Count; i++)
        {
            var f = p.ApprovalFlows[i];
            if (f is null) continue;
            var at = FlowAt(f, i);
            var module = f.Module ?? "";
            if (!modules.TryGetValue(module, out var m)) { errors.Add($"{at}.module: 模組 {module} 不在套件內"); continue; }
            if (m.Kind != ModuleKind.Standalone || FieldsOf(m).Any(IsDetailReference) || !workflows.TryGetValue(module, out var w))
            {
                errors.Add($"{at}.module: 簽核流程只能掛在有狀態機的獨立模組上");
                Incomplete(module, f.To, workflows.TryGetValue(module, out var mw) && mw.States.Any(s => s?.Option == f.To));
                continue;
            }
            var states = w.States.Where(s => s is not null).Select(s => s.Option).ToHashSet();
            var ok = true;
            var targetKnown = f.To is not null && states.Contains(f.To);
            if (f.To is null || f.To == w.Initial || !states.Contains(f.To)) { errors.Add($"{at}.to: {ApprovalMessages.TargetStateInvalid}"); ok = false; }
            if (f.RejectTo is not null && !states.Contains(f.RejectTo)) { errors.Add($"{at}.rejectTo: {ApprovalMessages.RejectToInvalidState}"); ok = false; }
            // 設計器允許這種狀態（取消轉移的需簽核、保留流程），匯出再匯入要讀得回來，所以只警告、不拒收（審查 S2，主控裁決）
            if (ok && !w.Transitions.Any(t => t is not null && t.To == f.To && t.Approval is { Required: true }))
                warnings.Add($"{at}.to: 狀態「{OptionLabel(fields, w, f.To!)}」沒有需簽核的轉移進入，這個流程不會被用到");
            if (ok && !priorities.Add((module, f.To!, f.Priority))) errors.Add($"{at}.priority: 同一目標狀態上的流程優先序不能重複");

            // 條件與欄位只認同一模組（§14.4 跨應用程式一點）：別的模組的欄位、主檔符號一律不收
            var lookups = new AppReportCodec.Lookups(
                code => code == module ? code : null,
                name => SplitQualified(name)?.Module == module && fields.ContainsKey(name) ? 1 : null,
                name => optionNames.Contains(name) ? 1 : null,
                AppReportCodec.TypesOf(p.Modules));
            void When(System.Text.Json.JsonElement? when, string wat)
            {
                if (IsEmpty(when)) return;
                foreach (var error in ConditionErrors(when!.Value, lookups))
                {
                    const string unknown = "條件引用了套件沒有的欄位 ";
                    var name = error.StartsWith(unknown, StringComparison.Ordinal) ? error[unknown.Length..] : null;
                    errors.Add(name is not null && fields.ContainsKey(name) ? $"{wat}: {name} 必須是模組 {module} 的欄位" : $"{wat}: {error}");
                }
            }
            void Groups(IEnumerable<string>? codes, string gat)
            {
                foreach (var g in (codes ?? []).Where(g => !groupCodes.Contains(g ?? ""))) errors.Add($"{gat}: 群組 {g} 沒有在 groups 宣告");
            }
            void Source(AppApproverSource? a, string sat)
            {
                if (a is null) return;
                Groups(a.Groups, sat);
                Groups(a.CandidateGroups, sat);
                if (a is { Source: ApprovalSources.UserField, Field: { } field }
                    && (SplitQualified(field)?.Module != module || !fields.TryGetValue(field, out var uf) || uf.Type != FieldType.User))
                    errors.Add($"{sat}: {field} 必須是模組 {module} 的使用者欄位");
            }

            When(f.When, $"{at}.when");
            Source(f.FallbackApprovers, $"{at}.fallbackApprovers");
            foreach (var s in (f.Stages ?? []).Where(s => s is not null))
            {
                var sat = $"{at}.stages[{s.Code}]";
                When(s.When, $"{sat}.when");
                Source(s.Approvers, $"{sat}.approvers");
                foreach (var name in (s.EditableFields ?? []).Where(n => SplitQualified(n)?.Module != module || !fields.ContainsKey(n ?? "")))
                    errors.Add($"{sat}.editableFields: {name} 必須是模組 {module} 的欄位");
            }
            if (ok) valid.Add(f);
            else Incomplete(module, f.To, targetKnown);
        }

        foreach (var (module, w) in workflows)
            if (valid.Any(f => f.Module == module) || w.Transitions.Any(t => t?.Approval is { Required: true }))
                Consistency(module, w, valid.Where(f => f.Module == module).ToList(), fields, errors,
                    incomplete.TryGetValue(module, out var skip) ? skip ?? w.States.Where(s => s?.Option is not null).Select(s => s.Option).ToHashSet() : []);
    }

    private static string OptionLabel(Dictionary<string, TemplateField> fields, AppWorkflow w, string option) =>
        fields.GetValueOrDefault(Qualify(w.Module, w.StatusField ?? ""))?.Options.FirstOrDefault(o => o.Code == option)?.Label.Resolve(I18nText.FallbackLocale) ?? option;

    /// <summary>
    /// 狀態一致性（J4，§14.4、§5.2）：用選項代號配發暫時 Id 組出 Workflow、只有狀態欄位的 Module 與 FlowState，交給 ApprovalConstraints；
    /// 錯誤鍵換回代號、加 workflows[{模組}]. 前綴。條件與簽核人來源不放進定義（閉包要看公式，留給開啟時），可編輯欄位只保留「有沒有」。
    /// 狀態或轉移有代號解析不到（V3 已報錯）就不檢查這個模組。
    /// </summary>
    /// <param name="incomplete">有流程沒通過引用檢查（已報錯）的目標狀態代號：這些狀態略過「沒有流程」「沒有啟用中的流程」兩類訊息。</param>
    private static void Consistency(
        string module, AppWorkflow w, List<AppApprovalFlow> flows, Dictionary<string, TemplateField> fields, List<string> errors,
        IReadOnlySet<string> incomplete)
    {
        if (!fields.TryGetValue(Qualify(module, w.StatusField ?? ""), out var status)) return;
        var ids = new Dictionary<string, long>();
        foreach (var o in status.Options.Where(o => o?.Code is not null)) ids.TryAdd(o.Code, ids.Count + 1);
        var codeOf = ids.ToDictionary(kv => kv.Value, kv => kv.Key);
        bool Known(string? code) => code is not null && ids.ContainsKey(code);
        if (!Known(w.Initial) || w.States.Any(s => !Known(s?.Option)) || w.Transitions.Any(t => !Known(t?.From) || !Known(t?.To))) return;

        var entity = new Module
        {
            Key = module,
            Blocks = [new Block { Fields = [new Field
            {
                Id = 0, Type = FieldType.Enum, Status = FieldStatus.Active, LabelI18n = status.Label,
                Options = status.Options.Where(o => o?.Code is not null && ids[o.Code] > 0).DistinctBy(o => o.Code)
                    .Select(o => new FieldOption { Id = ids[o.Code], LabelI18n = o.Label, Active = true }).ToList()
            }] }]
        };
        var wf = new Workflow
        {
            StatusFieldId = 0, InitialOptionId = ids[w.Initial],
            States = w.States.Select(s => new WorkflowState(ids[s.Option], s.ReadOnly, s.Editable ?? false)).ToList(),
            Transitions = w.Transitions.Select(t => new WorkflowTransition(ids[t.From], ids[t.To], [],
                t.Approval is { Required: true } a ? new TransitionApproval(true, a.NoMatch ?? ApprovalNoMatch.Block) : null)).ToList()
        };
        var set = flows.Select(f => new FlowState(
            new ApprovalFlow { Key = f.Code, NameI18n = f.Name, ToOptionId = ids[f.To], Priority = f.Priority, Active = f.Active },
            new ApprovalDefinition(ApprovalShapes.Sequence, null, f.RejectTo is { } rt ? ids[rt] : null, null,
                (f.Stages ?? []).Where(s => s is not null).Select(s => new ApprovalStageDef(s.Code, s.Name, null, new ApproverSource(ApprovalSources.Group),
                    s.Mode, (s.EditableFields ?? []).Count > 0 ? [-1] : [], null, ApprovalOnEmpty.Fallback)).ToList()))).ToList();

        var presence = status.Options.Where(o => o?.Label is not null && incomplete.Contains(o.Code)).Select(o => o.Label.Resolve(I18nText.FallbackLocale))
            .SelectMany(l => new[] { ApprovalMessages.NoFlow(l), ApprovalMessages.DirectNeedsActive(l) }).ToHashSet();
        string State(string id) => long.TryParse(id, out var n) && codeOf.TryGetValue(n, out var c) ? c : id;
        foreach (var (key, messages) in ApprovalConstraints.Validate(entity, wf, set))
        {
            var mapped = Regex.Replace(key, @"^transitions\[(\d+)>(\d+)\]$", m => $"transitions[{State(m.Groups[1].Value)}>{State(m.Groups[2].Value)}]");
            mapped = Regex.Replace(mapped, @"^states\[(\d+)\]$", m => $"states[{State(m.Groups[1].Value)}]");
            if (mapped == "initialOptionId") mapped = "initial";
            foreach (var message in messages.Where(m => !presence.Contains(m))) errors.Add($"workflows[{module}].{mapped}: {message}");
        }
    }
}
