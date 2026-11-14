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
    /// 局部錯誤（G1 屬性、G2 度數）不影響拓撲分析，其他規則照跑；只有可達（G3、G4）或環（G5）這類
    /// 「沒辦法可靠分析拓撲」的錯誤才跳過並行區分析（G7 區域、G8、G12、巢狀深度）。
    /// 匯合與分支的配對檢查只看節點本身，不依賴區域，一律執行。
    /// 重複代號的第二筆不進圖模型，但仍檢查局部項目（kind、屬性、連線端點、自環、G6 屬性），錯誤鍵用陣列索引（nodes[2].kind）；第一筆維持代號鍵。
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
            if (!seenNodes.Add(n.Id))
            {
                Err($"nodes[{i}].id", "G1", "idDuplicate");
                if (n.Kind is null || !Kinds.Contains(n.Kind)) Err($"nodes[{i}].kind", "G1", "kindInvalid");
                else CheckAttributes(n, $"nodes[{i}]", Err);
                continue;
            }
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
            var dupEdge = !seenEdges.Add(e.Id);
            if (dupEdge) Err($"edges[{i}].id", "G1", "idDuplicate");
            else edgeKey[i] = $"edges.{e.Id}";
            var ok = true;
            if (e.From is null || !seenNodes.Contains(e.From)) { Err($"{edgeKey[i]}.from", "G1", "edgeEndpointMissing"); ok = false; }
            if (e.To is null || !seenNodes.Contains(e.To)) { Err($"{edgeKey[i]}.to", "G1", "edgeEndpointMissing"); ok = false; }
            if (!ok) continue;
            if (e.From == e.To) { Err(edgeKey[i], "G1", "selfLoop"); continue; }
            if (dupEdge)
            {
                // 重複的連線不進圖模型，只做只看這條線本身的檢查；defaultCount、orderDuplicate 這類合計的檢查不算它。
                if (nodes.First(x => x.Item2.Id == e.From).Item2.Kind != ApprovalNodeKinds.Condition) CheckNonConditionEdge(e, edgeKey[i], Err);
                continue;
            }
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

        // G7 配對（不依賴區域，一律跑）；G7 區域、G8、G12、巢狀深度只在拓撲可靠時跑。
        var pairs = CheckPairing(m, NodeKey, Err);
        var topologyUnreliable = errors.Any(e => e.Rule is "G3" or "G4" or "G5");
        if (!topologyUnreliable) CheckRegions(m, pairs, NodeKey, EdgeKey, Err);

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

    /// <summary>非條件節點的出線不能帶 when／order／default（G6）。</summary>
    private static void CheckNonConditionEdge(ApprovalEdgeDef e, string key, Action<string, string, string> err)
    {
        if (HasValue(e.When)) err($"{key}.when", "G6", "conditionAttrNotAllowed");
        if (e.Order is not null) err($"{key}.order", "G6", "conditionAttrNotAllowed");
        if (e.Default is not null) err($"{key}.default", "G6", "conditionAttrNotAllowed");
    }

    private static void CheckConditions(
        ApprovalGraphModel m, string[] edgeKey, string[] nodeKey, Action<string, string, string> err)
    {
        foreach (var n in m.Nodes)
        {
            var outs = m.Out(n.Id);
            if (n.Kind != ApprovalNodeKinds.Condition)
            {
                foreach (var e in outs) CheckNonConditionEdge(e.Def, edgeKey[e.Index], err);
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

    /// <summary>G7 配對：每個分支恰有一個匯合以 forkId 指向它；回傳配對成功的 (分支, 匯合)。</summary>
    private static List<(ApprovalNodeDef Fork, ApprovalNodeDef Join)> CheckPairing(
        ApprovalGraphModel m, Func<ApprovalNodeDef, string> nodeKey, Action<string, string, string> err)
    {
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
        return joined;
    }

    private static void CheckRegions(
        ApprovalGraphModel m, List<(ApprovalNodeDef Fork, ApprovalNodeDef Join)> joined,
        Func<ApprovalNodeDef, string> nodeKey, Func<ApprovalGraphModel.Edge, string> edgeKey,
        Action<string, string, string> err)
    {
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
                // 防線：目前推導上不會觸發。區域 R 是「不經匯合能到的節點」的閉包，from ∈ R 且 to ≠ J 時，to 必然也在 R；
                // 繞過匯合的路徑在有限無環圖最後會走到結束，由 G8（endInRegion）報。保留是為了區域演算法日後改動時仍有守衛。
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
                            // 分支私下會合時同一節點會出現在兩條分支，不與自己比（那是 branchesMerge 的事）。
                            if (a.Id != b.Id && (a.EditableFields ?? []).Intersect(b.EditableFields ?? []).Any())
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
            // 豁免只算舊版同代號而且也是分支的節點；舊版是條件節點等其他種類，新版的分支視為新分支。
            var oldFork = oldNodes.FirstOrDefault(n => n?.Id == f.Id) is { Kind: ApprovalNodeKinds.Fork };
            var before = oldFork ? (old?.Edges ?? []).Count(e => e?.From == f.Id) : 0;
            if (count > limits.MaxBranches && count > before) err(nodeKey(f), "G13", "branchesTooMany");
        }
    }
}
