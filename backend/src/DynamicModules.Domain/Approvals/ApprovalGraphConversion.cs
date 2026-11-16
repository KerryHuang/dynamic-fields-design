namespace DynamicModules.Domain.Approvals;

/// <summary>sequence 與 graph 之間的轉換（流程圖設計 §7.8）；純函式。</summary>
public static class ApprovalGraphConversion
{
    /// <summary>
    /// start → 各關（依序，id = code，屬性原樣）→ end；連線 e1… 依序；頂層 when、rejectTo、fallbackApprovers 保留。
    /// 已是 graph 原樣回傳。關卡代號撞到 start 或 end 時，開始、結束節點依序改用 start-0、start-1…。
    /// 節點與連線是兩個命名空間，關卡代號 e1 不影響連線代號。
    /// </summary>
    public static ApprovalDefinition FromSequence(ApprovalDefinition d)
    {
        if (d.Shape == ApprovalShapes.Graph) return d;

        var stages = d.Stages ?? [];
        var taken = stages.Select(s => s.Code).ToHashSet();
        var start = Free("start", taken);
        var end = Free("end", taken);

        var nodes = new List<ApprovalNodeDef> { new(start, ApprovalNodeKinds.Start) };
        nodes.AddRange(stages.Select(s => new ApprovalNodeDef(
            s.Code, ApprovalNodeKinds.Approval, s.Name, s.When, s.Approvers, s.Mode, s.EditableFields, s.DueHours, s.OnEmpty)));
        nodes.Add(new ApprovalNodeDef(end, ApprovalNodeKinds.End));

        var edges = new List<ApprovalEdgeDef>();
        for (var i = 0; i < nodes.Count - 1; i++)
            edges.Add(new ApprovalEdgeDef($"e{i + 1}", nodes[i].Id, nodes[i + 1].Id));

        return new ApprovalDefinition(ApprovalShapes.Graph, d.When, d.RejectTo, d.FallbackApprovers, [], nodes, edges);
    }

    private static string Free(string baseId, HashSet<string> taken)
    {
        if (!taken.Contains(baseId)) return baseId;
        for (var i = 0; ; i++)
            if (!taken.Contains($"{baseId}-{i}")) return $"{baseId}-{i}";
    }
}
