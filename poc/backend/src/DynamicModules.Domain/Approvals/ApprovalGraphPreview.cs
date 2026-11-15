using System.Text.Json;

namespace DynamicModules.Domain.Approvals;

/// <summary>預計路徑上的一項：簽核節點，或 U9 的「依條件決定」（Undecided，NodeId 是條件節點）；AnyGroup 是任一到齊匯合的代號（U10）。</summary>
public sealed record PreviewStep(string NodeId, bool Undecided, string? AnyGroup);

/// <summary>Steps 依預計路徑的排列順序（分支依定義順序，不代表先後）；NodeIds、EdgeIds 是預計經過的節點與連線；
/// UndecidableNodeId 非 null 代表走到這個條件節點無法判斷。</summary>
public sealed record PreviewWalk(IReadOnlyList<PreviewStep> Steps, IReadOnlyList<string> NodeIds, IReadOnlyList<string> EdgeIds, string? UndecidableNodeId);

/// <summary>
/// 送出前預覽與設計器試算的預計路徑（流程圖設計 §8.9；計畫 fg2 J19、J26）。純函式、不碰資料庫。
/// </summary>
public static class ApprovalGraphPreview
{
    /// <summary>
    /// 預計路徑：從開始依 eval 走。條件節點的出線有 hidden 的條件（引用發起人看不到的欄位）→ 標 Undecided、不走它的出線，
    /// 從 <see cref="ResumeAfter"/> 接著走（U9），null 就停；all 匯合依定義順序列全部分支；any 匯合依 U10：
    /// 依定義順序找第一條「確定不經人工就到達」的分支（分支上的簽核節點 passesWithoutHuman 都為 true、沒有 Undecided），
    /// 有就只列那條分支（其後的分支引擎不會展開，也不求值），否則全部分支都列、帶 AnyGroup。
    /// Steps 含 passesWithoutHuman 為 true 的簽核節點，由呼叫端決定要不要列（設計器列 skip、記錄預覽不列）。
    /// 條件節點無法判斷就回 UndecidableNodeId（與引擎一樣是 409，不走預設線）。
    /// </summary>
    public static PreviewWalk Walk(ApprovalGraphRuntime g, Func<JsonElement, ConditionTruth> eval,
        Func<JsonElement, bool> hidden, Func<ApprovalNodeDef, bool> passesWithoutHuman)
    {
        var walker = new Walker(g, eval, hidden, passesWithoutHuman, new Postdominators(g));
        var seg = walker.Segment(g.Start.Id, null);
        return new PreviewWalk(seg.Steps, seg.NodeIds, seg.EdgeIds, walker.Undecidable);
    }

    /// <summary>J26：直接後必經節點（從它出發的每一條路都一定會經過、而且最先遇到的節點）；各條路在結束前沒有共同點就回 null。</summary>
    public static string? ResumeAfter(ApprovalGraphRuntime g, string nodeId) => new Postdominators(g).Immediate(nodeId);

    /// <summary>一段路徑的走訪結果；Reached 表示走到了停點（匯合），否則走到結束或中途停住。</summary>
    private sealed class Segment
    {
        public List<PreviewStep> Steps { get; } = [];
        public List<string> NodeIds { get; } = [];
        public List<string> EdgeIds { get; } = [];
        public bool Reached { get; set; }

        public void Add(Segment other)
        {
            Steps.AddRange(other.Steps);
            NodeIds.AddRange(other.NodeIds);
            EdgeIds.AddRange(other.EdgeIds);
        }
    }

    private sealed class Walker(ApprovalGraphRuntime g, Func<JsonElement, ConditionTruth> eval, Func<JsonElement, bool> hidden,
        Func<ApprovalNodeDef, bool> passes, Postdominators pdom)
    {
        public string? Undecidable { get; private set; }

        /// <summary>從 nodeId 走到 stop（配對的匯合，null＝一路走到結束）；走訪中遇到無法判斷的條件就停下並記下。</summary>
        public Segment Segment(string nodeId, string? stop)
        {
            var seg = new Segment();
            var id = nodeId;
            while (true)
            {
                if (id == stop) { seg.Reached = true; return seg; }
                var node = g.Node(id);
                seg.NodeIds.Add(id);
                switch (node.Kind)
                {
                    case ApprovalNodeKinds.End:
                        return seg;
                    case ApprovalNodeKinds.Condition when g.Out(id).Any(e => e.When is { ValueKind: JsonValueKind.Object } w && hidden(w)):
                        seg.Steps.Add(new PreviewStep(id, true, null));
                        if (pdom.Immediate(id) is not { } resume) return seg;
                        id = resume;
                        continue;
                    case ApprovalNodeKinds.Condition:
                    {
                        var (edge, undecidable) = g.Choose(id, eval);
                        if (undecidable) { Undecidable = id; return seg; }
                        seg.EdgeIds.Add(edge!.Id);
                        id = edge.To;
                        continue;
                    }
                    case ApprovalNodeKinds.Fork:
                    {
                        var join = g.JoinOf(id);
                        var arms = Arms(g.Out(id), join);
                        if (Undecidable is not null) return seg;
                        seg.Add(arms.Seg);
                        if (!arms.AllReached) return seg;
                        seg.EdgeIds.Add(g.Out(join.Id).Single().Id);
                        id = g.Out(join.Id).Single().To;
                        continue;
                    }
                    case ApprovalNodeKinds.Join:
                        return seg;
                    default:
                    {
                        // 開始、簽核：單一出線
                        if (node.Kind == ApprovalNodeKinds.Approval) seg.Steps.Add(new PreviewStep(id, false, null));
                        var next = g.Out(id).Single();
                        seg.EdgeIds.Add(next.Id);
                        id = next.To;
                        break;
                    }
                }
            }
        }

        private (Segment Seg, bool AllReached) Arms(IReadOnlyList<ApprovalEdgeDef> outs, ApprovalNodeDef join)
        {
            var any = join.Mode == ApprovalModes.Any;
            var result = new Segment();
            var armSegs = new List<Segment>();
            foreach (var edge in outs)
            {
                var arm = Segment(edge.To, join.Id);
                arm.EdgeIds.Insert(0, edge.Id);
                if (Undecidable is not null) return (result, false);
                if (any && arm.Reached && arm.Steps.All(s => !s.Undecided && passes(g.Node(s.NodeId))))
                {
                    // U10：確定立即到達，這條分支引擎展開時就通過匯合、取消其他分支，其他分支不列也不再求值
                    result.Add(arm);
                    result.NodeIds.Add(join.Id);
                    return (result, true);
                }
                armSegs.Add(arm);
            }
            foreach (var arm in armSegs)
            {
                // 任一到齊：每條分支的項目都帶匯合代號（內層已帶的保留）
                if (any) result.Steps.AddRange(arm.Steps.Select(s => s with { AnyGroup = s.AnyGroup ?? join.Id }));
                else result.Steps.AddRange(arm.Steps);
                result.NodeIds.AddRange(arm.NodeIds);
                result.EdgeIds.AddRange(arm.EdgeIds);
            }
            var allReached = armSegs.All(a => a.Reached);
            if (allReached) result.NodeIds.Add(join.Id);
            return (result, allReached);
        }
    }

    /// <summary>後必經節點（J26）：圖上加一個虛擬終點、每個結束節點接過去，依反向拓撲序做 pdom(n) = {n} ∪ ⋂ pdom(後繼)。整張圖只算一次。</summary>
    private sealed class Postdominators
    {
        private const string Sink = "\0end";
        private readonly Dictionary<string, HashSet<string>> _pdom = new() { [Sink] = [Sink] };

        public Postdominators(ApprovalGraphRuntime g)
        {
            // 後序走訪：後繼先於前驅（圖無環，G5）
            var order = new List<string>();
            var seen = new HashSet<string>();
            void Visit(string id)
            {
                if (!seen.Add(id)) return;
                foreach (var e in g.Out(id)) Visit(e.To);
                order.Add(id);
            }
            Visit(g.Start.Id);
            foreach (var id in order)
            {
                var succ = g.Out(id).Select(e => e.To).ToList();
                if (succ.Count == 0) succ.Add(Sink);
                var set = new HashSet<string>(_pdom[succ[0]]);
                foreach (var s in succ.Skip(1)) set.IntersectWith(_pdom[s]);
                set.Add(id);
                _pdom[id] = set;
            }
        }

        /// <summary>直接後必經節點：後必經集合（不含自己）裡集合最大的那個（鏈上離自己最近）；是虛擬終點就回 null。</summary>
        public string? Immediate(string id)
        {
            var best = _pdom[id].Where(p => p != id).MaxBy(p => _pdom[p].Count)!;
            return best == Sink ? null : best;
        }
    }
}
