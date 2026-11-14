using System.Text.Json;

namespace DynamicModules.Domain.Approvals;

/// <summary>
/// 流程定義引用的條件、欄位與群組，sequence 與 graph 共用一組讀取函式（流程圖設計 §16）。
/// fail closed：Stages 與 Nodes／Edges 一律都讀（正常定義只有一邊有值），寧可多引用，不漏掉。
/// </summary>
public static class ApprovalDefinitionRefs
{
    /// <summary>sequence 的關卡，加上 graph 的簽核節點（AsStage，代號 = 節點代號）。</summary>
    public static IEnumerable<ApprovalStageDef> Steps(ApprovalDefinition d) =>
        (d.Stages ?? []).Concat((d.Nodes ?? []).Where(n => n is { Kind: ApprovalNodeKinds.Approval }).Select(n => n.AsStage()));

    /// <summary>流程啟動條件、各步驟的略過條件、graph 連線上的條件（條件節點的出線）。</summary>
    public static IEnumerable<JsonElement> Conditions(ApprovalDefinition d)
    {
        if (d.When is { } w) yield return w;
        foreach (var s in Steps(d))
            if (s.When is { } sw) yield return sw;
        foreach (var e in d.Edges ?? [])
            if (e?.When is { } ew) yield return ew;
    }

    /// <summary>通知範本標題與內文（所有語系）引用的欄位 Id；不影響路線，不進閉包與鎖定。</summary>
    public static IEnumerable<long> NotifyFieldRefs(ApprovalDefinition d) =>
        (d.Nodes ?? []).Where(n => n?.Template is not null)
            .SelectMany(n => n.Template!.Title.Values.Values.Concat(n.Template.Body.Values.Values))
            .SelectMany(ApprovalNotifyTemplate.FieldIds);

    /// <summary>簽核人與候選群組、備援、通知收件人（group）的群組 Id。</summary>
    public static IEnumerable<long> GroupRefs(ApprovalDefinition d)
    {
        static IEnumerable<long> Of(ApproverSource? s) => (s?.GroupIds ?? []).Concat(s?.CandidateGroupIds ?? []);
        return Of(d.FallbackApprovers)
            .Concat(Steps(d).SelectMany(s => Of(s.Approvers)))
            .Concat((d.Nodes ?? []).SelectMany(n => n?.Recipients ?? []).SelectMany(r => r?.GroupIds ?? []));
    }
}
