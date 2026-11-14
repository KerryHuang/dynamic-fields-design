using System.Text.Json;

namespace DynamicModules.Domain.Approvals;

/// <summary>一個結構問題：錯誤鍵（J15）、規則（G1…）、代碼。不帶訊息文字，訊息由 Application 對照（J6）。</summary>
public sealed record GraphIssue(string Key, string Rule, string Code);

public sealed record GraphCheck(IReadOnlyList<GraphIssue> Errors, IReadOnlyList<GraphIssue> Warnings);

/// <summary>政策上限：簽核節點數、每個分支節點的分支數；由 Application 讀政策後傳入，Domain 不寫死數字。</summary>
public sealed record GraphPolicyLimits(int MaxApprovalNodes, int MaxBranches);

/// <summary>流程圖結構驗證 G1–G8、G12、G13（流程圖設計 §9）。簽核人、條件語法、範本等需要模組資料的規則在 Application。</summary>
public static class ApprovalGraphValidator
{
    private static readonly HashSet<string> Kinds =
    [
        ApprovalNodeKinds.Start, ApprovalNodeKinds.Approval, ApprovalNodeKinds.Condition, ApprovalNodeKinds.Fork,
        ApprovalNodeKinds.Join, ApprovalNodeKinds.End, ApprovalNodeKinds.Notify
    ];

    /// <summary>
    /// previous：這條流程目前版本（sequence 先轉換，J16）；政策上限超過且比舊版大才擋。Warnings 在 fg1d 才填。
    /// 有格式（G1、G2）、可達（G3、G4）或環（G5）問題時不跑並行區規則（G7、G8、G12），避免連帶錯誤。
    /// </summary>
    public static GraphCheck Check(ApprovalDefinition d, GraphPolicyLimits limits, ApprovalDefinition? previous)
    {
        var errors = new List<GraphIssue>();
        void Err(string key, string rule, string code) => errors.Add(new GraphIssue(key, rule, code));

        if (d.Stages is { Count: > 0 }) Err("graph", "G1", "stagesNotAllowed");
        if (d.Nodes is null)
        {
            Err("graph", "G1", "nodesMissing");
            return new GraphCheck(errors, []);
        }

        // G1 節點
        var nodeKey = new string[d.Nodes.Count];
        var nodes = new List<(int, ApprovalNodeDef)>();
        var seenNodes = new HashSet<string>();
        for (var i = 0; i < d.Nodes.Count; i++)
        {
            var n = d.Nodes[i];
            nodeKey[i] = $"nodes[{i}]";
            if (n is null || !ApprovalCodes.IsValid(n.Id)) { Err($"nodes[{i}].id", "G1", "idInvalid"); continue; }
            if (!seenNodes.Add(n.Id)) { Err($"nodes[{i}].id", "G1", "idDuplicate"); continue; }
            nodeKey[i] = $"nodes.{n.Id}";
            if (n.Kind is null || !Kinds.Contains(n.Kind)) Err(nodeKey[i], "G1", "kindInvalid");
            else CheckAttributes(n, nodeKey[i], Err);
            nodes.Add((i, n));
        }

        // G1 連線
        var rawEdges = d.Edges ?? [];
        var edgeKey = new string[rawEdges.Count];
        var edges = new List<(int, ApprovalEdgeDef)>();
        var seenEdges = new HashSet<string>();
        var seenPairs = new HashSet<(string, string)>();
        for (var i = 0; i < rawEdges.Count; i++)
        {
            var e = rawEdges[i];
            edgeKey[i] = $"edges[{i}]";
            if (e is null || !ApprovalCodes.IsValid(e.Id)) { Err($"edges[{i}].id", "G1", "idInvalid"); continue; }
            if (!seenEdges.Add(e.Id)) { Err($"edges[{i}].id", "G1", "idDuplicate"); continue; }
            edgeKey[i] = $"edges.{e.Id}";
            var ok = true;
            if (e.From is null || !seenNodes.Contains(e.From)) { Err($"{edgeKey[i]}.from", "G1", "edgeEndpointMissing"); ok = false; }
            if (e.To is null || !seenNodes.Contains(e.To)) { Err($"{edgeKey[i]}.to", "G1", "edgeEndpointMissing"); ok = false; }
            if (!ok) continue;
            if (e.From == e.To) { Err(edgeKey[i], "G1", "selfLoop"); continue; }
            if (!seenPairs.Add((e.From!, e.To!))) { Err(edgeKey[i], "G1", "edgeDuplicate"); continue; }
            edges.Add((i, e));
        }

        var m = new ApprovalGraphModel(nodes, edges);
        string NodeKey(ApprovalNodeDef n) => nodeKey[m.NodeOrder[n.Id]];
        string EdgeKey(ApprovalGraphModel.Edge e) => edgeKey[e.Index];

        // G2
        var starts = m.OfKind(ApprovalNodeKinds.Start).ToList();
        var ends = m.OfKind(ApprovalNodeKinds.End).ToList();
        if (starts.Count != 1) Err("graph", "G2", "startCount");
        if (ends.Count == 0) Err("graph", "G2", "endMissing");
        foreach (var n in m.Nodes)
        {
            var (inMin, inMax, outMin, outMax) = n.Kind switch
            {
                ApprovalNodeKinds.Start => (0, 0, 1, 1),
                ApprovalNodeKinds.Approval or ApprovalNodeKinds.Notify => (1, int.MaxValue, 1, 1),
                ApprovalNodeKinds.Condition or ApprovalNodeKinds.Fork => (1, int.MaxValue, 2, int.MaxValue),
                ApprovalNodeKinds.Join => (2, int.MaxValue, 1, 1),
                ApprovalNodeKinds.End => (1, int.MaxValue, 0, 0),
                _ => (0, int.MaxValue, 0, int.MaxValue)
            };
            var cin = m.In(n.Id).Count;
            var cout = m.Out(n.Id).Count;
            if (cin < inMin || cin > inMax) Err(NodeKey(n), "G2", "inCount");
            if (cout < outMin || cout > outMax) Err(NodeKey(n), "G2", "outCount");
        }

        // G3、G4、G5
        if (starts.Count > 0)
        {
            var reach = m.Reach(starts.Select(s => s.Id));
            foreach (var n in m.Nodes.Where(n => !reach.Contains(n.Id))) Err(NodeKey(n), "G3", "unreachable");
        }
        if (ends.Count > 0)
        {
            var canEnd = m.Reach(ends.Select(s => s.Id), reverse: true);
            foreach (var n in m.Nodes.Where(n => !canEnd.Contains(n.Id))) Err(NodeKey(n), "G4", "deadEnd");
        }
        if (!m.IsAcyclic()) Err("graph", "G5", "cycle");

        // G6
        CheckConditions(m, edgeKey, nodeKey, Err);

        // G7、G8、G12、巢狀深度
        var structural = errors.Count > 0 && errors.Any(e => e.Rule is "G1" or "G2" or "G3" or "G4" or "G5");
        if (!structural) CheckRegions(m, NodeKey, EdgeKey, Err);

        // G13
        CheckCounts(d, m, limits, previous, NodeKey, Err);

        return new GraphCheck([.. errors.Distinct()], []);
    }

    /// <summary>節點只能帶自己 kind 的屬性；放錯位置的屬性不會被參照收集讀到（只讀簽核節點），所以一律拒收。</summary>
    private static void CheckAttributes(ApprovalNodeDef n, string key, Action<string, string, string> err)
    {
        var approval = n.Kind == ApprovalNodeKinds.Approval;
        var join = n.Kind == ApprovalNodeKinds.Join;
        var notify = n.Kind == ApprovalNodeKinds.Notify;
        void Check(bool present, bool allowed, string prop)
        {
            if (present && !allowed) err($"{key}.{prop}", "G1", "attrNotAllowed");
        }
        Check(HasValue(n.When), approval, "when");
        Check(n.Approvers is not null, approval, "approvers");
        Check(n.Mode is not null, approval || join, "mode");
        Check(n.EditableFields is not null, approval, "editableFields");
        Check(n.DueHours is not null, approval, "dueHours");
        Check(n.OnEmpty is not null, approval, "onEmpty");
        Check(n.ForkId is not null, join, "forkId");
        Check(n.Recipients is not null, notify, "recipients");
        Check(n.Template is not null, notify, "template");
    }

    private static bool HasValue(JsonElement? e) => e is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) };

    private static void CheckConditions(
        ApprovalGraphModel m, string[] edgeKey, string[] nodeKey, Action<string, string, string> err)
    {
        foreach (var n in m.Nodes)
        {
            var outs = m.Out(n.Id);
            if (n.Kind != ApprovalNodeKinds.Condition)
            {
                foreach (var e in outs)
                {
                    var k = edgeKey[e.Index];
                    if (HasValue(e.Def.When)) err($"{k}.when", "G6", "conditionAttrNotAllowed");
                    if (e.Def.Order is not null) err($"{k}.order", "G6", "conditionAttrNotAllowed");
                    if (e.Def.Default is not null) err($"{k}.default", "G6", "conditionAttrNotAllowed");
                }
                continue;
            }

            if (outs.Count(e => e.Def.Default == true) != 1) err(nodeKey[m.NodeOrder[n.Id]], "G6", "defaultCount");
            var orders = new HashSet<int>();
            foreach (var e in outs)
            {
                var k = edgeKey[e.Index];
                if (e.Def.Default == true)
                {
                    if (HasValue(e.Def.When)) err($"{k}.when", "G6", "defaultHasWhen");
                    continue;
                }
                if (!HasValue(e.Def.When)) err($"{k}.when", "G6", "whenMissing");
                if (e.Def.Order is not { } o || o < 1) err($"{k}.order", "G6", "orderInvalid");
                else if (!orders.Add(o)) err($"{k}.order", "G6", "orderDuplicate");
            }
        }
    }

    private sealed record Pair(ApprovalNodeDef Fork, ApprovalNodeDef Join, HashSet<string> Region, List<HashSet<string>> Branches);

    private static void CheckRegions(
        ApprovalGraphModel m, Func<ApprovalNodeDef, string> nodeKey, Func<ApprovalGraphModel.Edge, string> edgeKey,
        Action<string, string, string> err)
    {
        // G7 配對：每個分支恰有一個匯合以 forkId 指向它。
        var claimed = new HashSet<string>();
        var joined = new List<(ApprovalNodeDef Fork, ApprovalNodeDef Join)>();
        foreach (var j in m.OfKind(ApprovalNodeKinds.Join))
        {
            if (j.Mode is not (ApprovalModes.All or ApprovalModes.Any)) err($"{nodeKey(j)}.mode", "G7", "joinModeInvalid");
            if (j.ForkId is null || !m.ById.TryGetValue(j.ForkId, out var f) || f.Kind != ApprovalNodeKinds.Fork || !claimed.Add(f.Id))
                err($"{nodeKey(j)}.forkId", "G7", "joinForkInvalid");
            else
                joined.Add((f, j));
        }
        foreach (var f in m.OfKind(ApprovalNodeKinds.Fork).Where(f => !claimed.Contains(f.Id)))
            err(nodeKey(f), "G7", "forkWithoutJoin");

        // 並行區：從分支出發、不經配對匯合能到的節點（不含分支與匯合）。
        var pairs = joined.OrderBy(p => m.NodeOrder[p.Fork.Id]).Select(p => new Pair(
            p.Fork, p.Join,
            m.Reach(m.Out(p.Fork.Id).Select(e => e.Def.To), p.Join.Id),
            [.. m.Out(p.Fork.Id).Select(e => m.Reach([e.Def.To], p.Join.Id))])).ToList();

        foreach (var p in pairs)
        {
            foreach (var n in m.Nodes.Where(n => n.Kind == ApprovalNodeKinds.End && p.Region.Contains(n.Id)))
                err(nodeKey(n), "G8", "endInRegion");
            foreach (var e in m.Edges)
            {
                var (from, to) = (e.Def.From, e.Def.To);
                if (p.Region.Contains(from) && !p.Region.Contains(to) && to != p.Join.Id) err(edgeKey(e), "G7", "edgeOutOfRegion");
                if (!p.Region.Contains(from) && from != p.Fork.Id && p.Region.Contains(to)) err(edgeKey(e), "G7", "edgeIntoRegion");
                if (to == p.Join.Id && !p.Region.Contains(from) && from != p.Fork.Id) err(edgeKey(e), "G7", "joinInEdgeOutside");
            }
            for (var i = 0; i < p.Branches.Count; i++)
                for (var j = i + 1; j < p.Branches.Count; j++)
                    if (p.Branches[i].Overlaps(p.Branches[j])) err(nodeKey(p.Fork), "G7", "branchesMerge");
        }

        for (var a = 0; a < pairs.Count; a++)
            for (var b = a + 1; b < pairs.Count; b++)
            {
                var (x, y) = (pairs[a], pairs[b]);
                var all = (Pair p) => p.Region.Append(p.Fork.Id).Append(p.Join.Id).ToHashSet();
                if (!all(x).Overlaps(all(y))) continue;
                if (!Inside(y, x) && !Inside(x, y)) err(nodeKey(x.Fork), "G7", "regionCross");
            }

        foreach (var p in pairs)
            if (1 + pairs.Count(q => q != p && q.Region.Contains(p.Fork.Id)) > ApprovalGraphLimits.MaxNesting)
                err(nodeKey(p.Fork), "G13", "nestingTooDeep");

        // G12 並行區內不同分支的簽核節點，可編輯欄位不得重疊；報後出現的那個。
        foreach (var p in pairs)
            for (var i = 0; i < p.Branches.Count; i++)
                for (var j = i + 1; j < p.Branches.Count; j++)
                    foreach (var a in Approvals(m, p.Branches[i]))
                        foreach (var b in Approvals(m, p.Branches[j]))
                            if ((a.EditableFields ?? []).Intersect(b.EditableFields ?? []).Any())
                                err($"{nodeKey(m.NodeOrder[a.Id] > m.NodeOrder[b.Id] ? a : b)}.editableFields", "G12", "editableOverlap");
    }

    private static bool Inside(Pair inner, Pair outer) =>
        outer.Region.Contains(inner.Fork.Id) && outer.Region.Contains(inner.Join.Id) && inner.Region.IsSubsetOf(outer.Region);

    private static IEnumerable<ApprovalNodeDef> Approvals(ApprovalGraphModel m, HashSet<string> ids) =>
        ids.Select(id => m.ById[id]).Where(n => n.Kind == ApprovalNodeKinds.Approval);

    private static void CheckCounts(
        ApprovalDefinition d, ApprovalGraphModel m, GraphPolicyLimits limits, ApprovalDefinition? previous,
        Func<ApprovalNodeDef, string> nodeKey, Action<string, string, string> err)
    {
        var old = previous is null ? null : ApprovalGraphConversion.FromSequence(previous);
        var oldNodes = old?.Nodes ?? [];
        var oldApprovals = oldNodes.Count(n => n?.Kind == ApprovalNodeKinds.Approval);

        var approvals = d.Nodes!.Count(n => n?.Kind == ApprovalNodeKinds.Approval);
        if (approvals == 0) err("graph", "G13", "approvalNodesNone");
        if (approvals > limits.MaxApprovalNodes && approvals > oldApprovals) err("graph", "G13", "approvalNodesTooMany");
        if (d.Nodes!.Count > ApprovalGraphLimits.MaxNodes) err("graph", "G13", "nodesTooMany");
        if ((d.Edges?.Count ?? 0) > ApprovalGraphLimits.MaxEdges) err("graph", "G13", "edgesTooMany");

        foreach (var f in m.OfKind(ApprovalNodeKinds.Fork))
        {
            var count = m.Out(f.Id).Count;
            var before = (old?.Edges ?? []).Count(e => e?.From == f.Id);
            if (count > limits.MaxBranches && count > before) err(nodeKey(f), "G13", "branchesTooMany");
        }
    }
}
