using System.Text.Json;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 流程圖格式的定義驗證（流程圖設計 §9）：結構規則交給 Domain 的 ApprovalGraphValidator（只回鍵、規則、代碼，這裡轉成訊息），
/// 這裡補需要模組資料的規則：簽核節點屬性（G9，與關卡同一組）、條件語法（G6）、通知節點參數與範本（G10）。
/// 錯誤鍵：nodes.{id}.{屬性}、edges.{id}.{屬性}、graph；代號不合法或重複的那一筆用陣列索引（nodes[2].kind），與 Domain 一致。
/// 警告 W1–W3 不在這裡回傳（fg3 的發布回應才帶）。
/// </summary>
public sealed partial class ApprovalDefinitionValidator
{
    private static void GraphErrors(Run x)
    {
        var (d, limits) = (x.D, x.Limits);
        // 結構錯誤先加：同一個鍵有多則時取第一則，結構問題比屬性細節更根本。
        foreach (var issue in ApprovalGraphValidator.Check(d, new GraphPolicyLimits(limits.MaxStages, limits.MaxBranches), x.Previous).Errors)
            x.Err(issue.Key, ApprovalMessages.Graph(issue.Code, limits.MaxStages, limits.MaxBranches));

        var old = x.Previous is null ? null : ApprovalGraphConversion.FromSequence(x.Previous);
        var kindOf = new Dictionary<string, string?>();

        var nodes = d.Nodes ?? [];
        var seen = new HashSet<string>();
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is not { } n) continue;
            var first = ApprovalCodes.IsValid(n.Id) && seen.Add(n.Id);
            var p = first ? $"nodes.{n.Id}" : $"nodes[{i}]";
            if (first) kindOf[n.Id] = n.Kind;
            var before = first ? old?.Nodes?.FirstOrDefault(o => o?.Id == n.Id) : null;
            switch (n.Kind)
            {
                case ApprovalNodeKinds.Approval:
                    if (n.Name is null || !n.Name.IsValid(out _)) x.Err($"{p}.name", ApprovalMessages.NodeNameRequired);
                    x.StageErrors(n.AsStage(), p, before is { Kind: ApprovalNodeKinds.Approval } ? before.AsStage() : null);
                    break;
                case ApprovalNodeKinds.Notify:
                    NotifyErrors(x, n, p, before is { Kind: ApprovalNodeKinds.Notify } ? before : null);
                    break;
                default:
                    // 條件、分支、匯合、開始、結束：名稱選填，有填才驗（U6）
                    if (n.Name is not null && !n.Name.IsValid(out _)) x.Err($"{p}.name", ApprovalMessages.NodeNameRequired);
                    break;
            }
        }

        var edges = d.Edges ?? [];
        var seenEdges = new HashSet<string>();
        for (var i = 0; i < edges.Count; i++)
        {
            if (edges[i] is not { } e) continue;
            var k = ApprovalCodes.IsValid(e.Id) && seenEdges.Add(e.Id) ? $"edges.{e.Id}" : $"edges[{i}]";
            if (e.Label is not null && !e.Label.IsValid(out _)) x.Err($"{k}.label", ApprovalMessages.EdgeLabelInvalid);
            // 只有條件節點的出線有條件；其他節點出線帶條件是 Domain 的 conditionAttrNotAllowed
            if (e.When is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } w
                && e.From is not null && kindOf.GetValueOrDefault(e.From) == ApprovalNodeKinds.Condition
                && x.ConditionError(w) is { } we)
                x.Err($"{k}.when", ApprovalMessages.WhenInvalid(we));
        }

        // fg2 之前流程圖一律不收；fg2 刪這一行就開放（計畫 J9）
        x.Err("shape", ApprovalMessages.GraphNotAccepted);
    }

    /// <summary>通知節點（G10）：名稱必填、收件人 1 到 20 項且參數合法、範本標題與內文語法正確並只引用本模組啟用中的欄位。</summary>
    private static void NotifyErrors(Run x, ApprovalNodeDef n, string p, ApprovalNodeDef? before)
    {
        if (n.Name is null || !n.Name.IsValid(out _)) x.Err($"{p}.name", ApprovalMessages.NodeNameRequired);

        var recipients = n.Recipients;
        if (recipients is null || recipients.Count == 0 || recipients.Count > ApprovalGraphLimits.MaxNotifyRecipients)
            x.Err($"{p}.recipients", ApprovalMessages.NotifyRecipientsCount(ApprovalGraphLimits.MaxNotifyRecipients));
        // 改嚴只管之後：主管層數超過政策但不比舊版同一節點的大就放行
        var oldLevel = (before?.Recipients ?? []).Where(o => o is { Kind: ApprovalNotifyRecipientKinds.InitiatorManager }).Select(o => o.Level ?? 0).DefaultIfEmpty(0).Max();
        foreach (var r in recipients ?? [])
            if (RecipientError(x, r, oldLevel) is { } re) x.Err($"{p}.recipients", re);

        TemplateText(x, n.Template?.Title, ApprovalGraphLimits.MaxTemplateTitleLength, $"{p}.template.title");
        TemplateText(x, n.Template?.Body, ApprovalGraphLimits.MaxTemplateBodyLength, $"{p}.template.body");
    }

    private static string? RecipientError(Run x, NotifyRecipientDef? r, int oldLevel)
    {
        if (r is null) return ApprovalMessages.NotifyRecipientInvalid;
        var (hasUsers, hasGroups, hasLevel) = (r.UserIds is not null, r.GroupIds is not null, r.Level is not null);
        switch (r.Kind)
        {
            case ApprovalNotifyRecipientKinds.Users:
                if (hasGroups || hasLevel) return ApprovalMessages.NotifyRecipientInvalid;
                return r.UserIds is { Count: > 0 } ids && ids.All(x.ActiveUsers.Contains) ? null : ApprovalMessages.UsersInvalid;
            case ApprovalNotifyRecipientKinds.Group:
                if (hasUsers || hasLevel) return ApprovalMessages.NotifyRecipientInvalid;
                return r.GroupIds is { Count: > 0 } g && g.All(x.GroupIds.Contains) ? null : ApprovalMessages.GroupsInvalid;
            case ApprovalNotifyRecipientKinds.Initiator:
                return hasUsers || hasGroups || hasLevel ? ApprovalMessages.NotifyRecipientInvalid : null;
            case ApprovalNotifyRecipientKinds.InitiatorManager:
                if (hasUsers || hasGroups) return ApprovalMessages.NotifyRecipientInvalid;
                // 層數沒填照設計 §12.1 預設第 1 層，再驗政策上限
                return (r.Level ?? 1) is var lv && lv >= 1 && (lv <= x.Limits.MaxManagerLevel || lv <= oldLevel)
                    ? null : ApprovalMessages.ManagerLevel(x.Limits.MaxManagerLevel, x.Previous is not null);
            default:
                return ApprovalMessages.NotifyRecipientInvalid;
        }
    }

    /// <summary>範本一段文字（標題或內文）：繁中必填、每個語系不超過上限、佔位符語法正確（U8）、{f:Id} 是本模組啟用中的欄位。</summary>
    private static void TemplateText(Run x, Domain.ValueObjects.I18nText? text, int max, string key)
    {
        if (text is null || !text.IsValid(out _)) { x.Err(key, ApprovalMessages.TemplateRequired); return; }
        foreach (var s in text.Values.Values.Select(v => v ?? ""))
        {
            if (s.Length > max) { x.Err(key, ApprovalMessages.TemplateTooLong(max)); return; }
            var (_, error) = ApprovalNotifyTemplate.Parse(s);
            if (error is not null) { x.Err(key, ApprovalMessages.TemplatePlaceholderUnknown(error)); return; }
            if (ApprovalNotifyTemplate.FieldIds(s).Any(id => !x.Fields.TryGetValue(id, out var f) || f.Status != FieldStatus.Active))
            { x.Err(key, ApprovalMessages.TemplateFieldInvalid); return; }
        }
    }
}
