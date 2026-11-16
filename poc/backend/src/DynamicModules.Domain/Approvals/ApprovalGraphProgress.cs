using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Approvals;

/// <summary>進度圖節點的顯示狀態（計畫 fg4 J16）；Changed、Hidden 由 Application 遮蔽時產生，<see cref="ApprovalGraphProgress.Of"/> 不會回。</summary>
public static class ApprovalProgressStates
{
    public const string Passed = "passed", Active = "active", Returned = "returned", Skipped = "skipped", Cancelled = "cancelled", Ended = "ended",
        NotReached = "notReached", Changed = "changed", Hidden = "hidden";
}

/// <summary>進度圖節點的 Kind：定義的七種，加上只供顯示的加簽小節點。</summary>
public static class ApprovalProgressKinds
{
    public const string AddSign = "addSign";
}

/// <summary>RunIds 只給簽核節點與加簽小節點（依 Seq 升冪）；JoinMode 只給匯合；HostId 只給加簽小節點（它掛的簽核節點代號）。</summary>
public sealed record ProgressNode(string Id, string Kind, I18nText? Name, string State, IReadOnlyList<long> RunIds, string? JoinMode, string? HostId);

public sealed record ProgressEdge(string Id, string From, string To, I18nText? Label, bool Traversed);

public sealed record ApprovalProgress(IReadOnlyList<ProgressNode> Nodes, IReadOnlyList<ProgressEdge> Edges);

/// <summary>
/// 進度圖的節點狀態、加簽小節點與走過的線（計畫 fg4 J4、J10、J15、J16；流程圖設計 §11）。純函式、全函式：
/// 資料對不上（重複代號、線的端點不存在、節點實例指向定義裡沒有的節點或線）回 null，不丟例外（M8）。不讀任務表。
/// </summary>
public static class ApprovalGraphProgress
{
    private static readonly IReadOnlySet<string> AllKinds = new HashSet<string>
    {
        ApprovalNodeKinds.Start, ApprovalNodeKinds.Approval, ApprovalNodeKinds.Condition, ApprovalNodeKinds.Fork,
        ApprovalNodeKinds.Join, ApprovalNodeKinds.End, ApprovalNodeKinds.Notify
    };

    /// <summary>
    /// shown：顯示用的定義（graph 照實例綁定的；sequence 先經 <see cref="ApprovalGraphConversion.FromSequence"/>；備援組三個節點）。
    /// byStageCode：sequence 與備援以 StageCode 對應節點，graph 以 NodeId。rejectedRunIds：有被退回任務的節點實例（Domain 不碰任務表）。
    /// instanceApproved：sequence 與備援的結束節點與進入它的線，只在實例核准時走到。
    /// </summary>
    public static ApprovalProgress? Of(ApprovalDefinition shown, IReadOnlyList<ApprovalStageRun> runs, IReadOnlySet<long> rejectedRunIds,
        bool byStageCode, bool instanceApproved)
    {
        if (shown.Nodes is not { } nodes || shown.Edges is not { } edges) return null;

        // ── 一致性檢查（J4、M8）：之後的輔助才不會丟例外 ──
        var nodeById = new Dictionary<string, ApprovalNodeDef>();
        foreach (var n in nodes)
            if (n.Id is null || !nodeById.TryAdd(n.Id, n)) return null;
        var edgeIds = new HashSet<string>();
        foreach (var e in edges)
            if (e.Id is null || !edgeIds.Add(e.Id) || e.From is null || e.To is null || !nodeById.ContainsKey(e.From) || !nodeById.ContainsKey(e.To)) return null;

        var ordered = runs.OrderBy(r => r.Seq).ToList();
        foreach (var r in ordered)
        {
            if (r.StageCode is null)
            {
                // 加簽列：graph 一律帶主節點（簽核型別）；sequence 的掛點在下面依 Seq 找
                if (!byStageCode && (r.NodeId is null || !nodeById.TryGetValue(r.NodeId, out var host) || host.Kind != ApprovalNodeKinds.Approval)) return null;
            }
            else
            {
                var key = byStageCode ? r.StageCode : r.NodeId;
                if (key is null || !nodeById.TryGetValue(key, out var node)) return null;
                if (!byStageCode && r.NodeKind is not null && r.NodeKind != node.Kind) return null;
            }
            if (!byStageCode && r.InEdgeId is not null && !edgeIds.Contains(r.InEdgeId)) return null;
        }

        // ── 分組：主節點的列、加簽小節點 ──
        var mainRuns = new Dictionary<string, List<ApprovalStageRun>>();
        var smalls = new Dictionary<string, List<SmallNode>>();   // 主節點代號 → 小節點（依第一列的 Seq）
        foreach (var r in ordered)
        {
            if (r.StageCode is not null)
            {
                var key = byStageCode ? r.StageCode : r.NodeId!;
                if (!mainRuns.TryGetValue(key, out var list)) mainRuns[key] = list = [];
                list.Add(r);
                continue;
            }
            // 從沒啟動就被關掉的排隊加簽不畫（M1）
            if (r.Status == ApprovalStageStatus.Closed && r.ActivatedTime is null) continue;
            var hostId = byStageCode ? HostOf(ordered, r) : r.NodeId;
            if (hostId is null || !nodeById.ContainsKey(hostId)) return null;
            var identity = (hostId, r.AddedByUserId, string.Join("\u0001", (r.AddSignUserIds ?? []).Order(StringComparer.Ordinal)), r.Mode);
            if (!smalls.TryGetValue(hostId, out var group)) smalls[hostId] = group = [];
            var small = group.FirstOrDefault(s => s.Identity == identity);
            if (small is null) group.Add(small = new SmallNode(identity));
            small.Runs.Add(r);
        }

        // ── 被取消分支段裡沒走到的節點（U5）：只有 graph，而且結構再驗通得過時才補 ──
        HashSet<string> cancelledUnreached = [];
        if (!byStageCode && ApprovalGraphRuntime.Build(shown, AllKinds, out _) is { } g)
        {
            try
            {
                foreach (var root in ApprovalGraphPath.CancelledRoots(g, ordered))
                {
                    var edge = g.Edge(root[(root.LastIndexOf('/') + 1)..]);
                    var join = g.JoinOf(edge.From).Id;
                    var stack = new Stack<string>([edge.To]);
                    while (stack.Count > 0)
                    {
                        var id = stack.Pop();
                        if (id == join || !cancelledUnreached.Add(id)) continue;
                        foreach (var next in g.Out(id)) stack.Push(next.To);
                    }
                }
            }
            catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException or InvalidOperationException)
            {
                return null;   // 節點實例與定義對不上（例如匯合到達列的進線不屬於這個匯合）
            }
        }

        string RunState(ApprovalStageRun r) => r.Status switch
        {
            ApprovalStageStatus.Approved or ApprovalStageStatus.Passed => ApprovalProgressStates.Passed,
            ApprovalStageStatus.Active or ApprovalStageStatus.Waiting => ApprovalProgressStates.Active,
            ApprovalStageStatus.Returned => ApprovalProgressStates.Returned,
            ApprovalStageStatus.Skipped => ApprovalProgressStates.Skipped,
            ApprovalStageStatus.Closed => rejectedRunIds.Contains(r.Id) ? ApprovalProgressStates.Returned
                : r.CloseReason == ApprovalCloseReasons.BranchCancelled ? ApprovalProgressStates.Cancelled : ApprovalProgressStates.Ended,
            _ => ApprovalProgressStates.NotReached
        };

        string StateOf(ApprovalNodeDef n)
        {
            if (n.Kind == ApprovalNodeKinds.Start) return ApprovalProgressStates.Passed;
            if (byStageCode && n.Kind == ApprovalNodeKinds.End) return instanceApproved ? ApprovalProgressStates.Passed : ApprovalProgressStates.NotReached;
            if (!mainRuns.TryGetValue(n.Id, out var own))
                return cancelledUnreached.Contains(n.Id) ? ApprovalProgressStates.Cancelled : ApprovalProgressStates.NotReached;
            // 匯合的到達列一條分支一列（J30）：任一列 passed 就是通過
            if (n.Kind == ApprovalNodeKinds.Join && own.Any(r => r.Status == ApprovalStageStatus.Passed)) return ApprovalProgressStates.Passed;
            return RunState(own[^1]);
        }

        // ── 節點：照定義順序，加簽小節點緊接在主節點後面 ──
        var outNodes = new List<ProgressNode>();
        foreach (var n in nodes)
        {
            IReadOnlyList<long> ids = n.Kind == ApprovalNodeKinds.Approval && mainRuns.TryGetValue(n.Id, out var mine) ? [.. mine.Select(r => r.Id)] : [];
            outNodes.Add(new ProgressNode(n.Id, n.Kind, n.Name, StateOf(n), ids, n.Kind == ApprovalNodeKinds.Join ? n.Mode : null, null));
            if (!smalls.TryGetValue(n.Id, out var group)) continue;
            foreach (var s in group)
                outNodes.Add(new ProgressNode(s.Id, ApprovalProgressKinds.AddSign, null, RunState(s.Runs[^1]), [.. s.Runs.Select(r => r.Id)], null, n.Id));
        }

        // ── 線：走過的線（J15）；加簽串線取代主節點原本出線的起點（J10） ──
        var traversed = byStageCode
            ? edges.Where(e => mainRuns.ContainsKey(e.To) || nodeById[e.To].Kind == ApprovalNodeKinds.End && instanceApproved).Select(e => e.Id).ToHashSet()
            : ordered.Where(r => r.InEdgeId is not null).Select(r => r.InEdgeId!).ToHashSet();

        var outEdges = new List<ProgressEdge>();
        var chained = new HashSet<string>();
        foreach (var e in edges)
        {
            var from = e.From;
            if (smalls.TryGetValue(e.From, out var group))
            {
                // 串線放在被取代的第一條出線的位置；主節點的出線改從最後一個小節點出發
                if (chained.Add(e.From)) outEdges.AddRange(Chain(e.From, group));
                from = group[^1].Id;
            }
            outEdges.Add(new ProgressEdge(e.Id, from, e.To, e.Label, traversed.Contains(e.Id)));
        }
        // 主節點沒有出線（結構不合法）：串到最後一個小節點為止
        foreach (var (host, group) in smalls)
            if (chained.Add(host)) outEdges.AddRange(Chain(host, group));

        return new ApprovalProgress(outNodes, outEdges);
    }

    private static IEnumerable<ProgressEdge> Chain(string host, List<SmallNode> group)
    {
        var from = host;
        foreach (var s in group)
        {
            yield return new ProgressEdge($"{s.Id}-in", from, s.Id, null, s.Runs.Any(r => r.ActivatedTime is not null));
            from = s.Id;
        }
    }

    /// <summary>sequence 與備援的加簽掛點：Seq 比它小、最近的一列有 StageCode 而且不是 returned 的節點實例（審查 M1a：退回重建的加簽不能掛在被退回的那一關）。</summary>
    private static string? HostOf(List<ApprovalStageRun> ordered, ApprovalStageRun addSign) =>
        ordered.LastOrDefault(r => r.Seq < addSign.Seq && r.StageCode is not null && r.Status != ApprovalStageStatus.Returned)?.StageCode;

    private sealed class SmallNode((string Host, string? AddedBy, string Users, string Mode) identity)
    {
        public (string Host, string? AddedBy, string Users, string Mode) Identity { get; } = identity;
        public List<ApprovalStageRun> Runs { get; } = [];
        public string Id => $"as-{Runs[0].Id}";
    }
}
