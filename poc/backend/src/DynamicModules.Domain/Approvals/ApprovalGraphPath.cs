using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Approvals;

/// <summary>
/// graph 實例的節點實例位置函式（流程圖設計 §8.4、§8.5；計畫 fg2 J4、J12、J13）：分支段內的有效路徑、上一關、
/// 被任一到齊取消的分支、匯合已到達的分支、撤回資格。純函式，只看節點實例與任務，不碰資料庫。
/// BranchKey 是分支段代號（<see cref="ApprovalGraphRuntime.Child"/>），null 與空字串都是根段。
/// </summary>
public static class ApprovalGraphPath
{
    private static string Key(ApprovalStageRun r) => r.BranchKey ?? "";

    private static bool IsBarrier(ApprovalStageRun r) => r.NodeKind is ApprovalNodeKinds.Fork or ApprovalNodeKinds.Join;

    /// <summary>段內有效路徑（Seq 升冪；approved、skipped、passed 進路徑；returned 依 <see cref="ApprovalPath.Effective"/> 的規則截斷）。</summary>
    public static IReadOnlyList<ApprovalStageRun> Segment(IReadOnlyList<ApprovalStageRun> runs, string branchKey) =>
        ApprovalPath.Effective([.. runs.Where(r => Key(r) == branchKey)], includePassed: true);

    /// <summary>
    /// J12：與 current 同段、Seq 比它小、在該段最後一列分支或匯合節點實例之後、段內有效路徑上最近一個 approved；沒有回 null。
    /// 分支內第一個簽核節點、匯合後第一個簽核節點因此沒有上一關。
    /// </summary>
    public static ApprovalStageRun? Previous(IReadOnlyList<ApprovalStageRun> runs, ApprovalStageRun current)
    {
        var key = Key(current);
        var before = runs.Where(r => r.Seq <= current.Seq).ToList();
        var barrier = before.Where(r => Key(r) == key && r.Seq < current.Seq && IsBarrier(r)).Select(r => r.Seq).DefaultIfEmpty(int.MinValue).Max();
        return Segment(before, key).LastOrDefault(r => r.Id != current.Id && r.Seq > barrier && r.Status == ApprovalStageStatus.Approved);
    }

    /// <summary>J13：被任一到齊取消的分支段根（到達的分支以 ArmOf 識別；同一分支節點的其他出線開出的段）。</summary>
    public static IReadOnlySet<string> CancelledRoots(ApprovalGraphRuntime g, IReadOnlyList<ApprovalStageRun> runs)
    {
        var roots = new HashSet<string>();
        foreach (var r in runs.Where(r => r.NodeKind == ApprovalNodeKinds.Join && r.Status == ApprovalStageStatus.Passed))
        {
            if (g.Node(r.NodeId!).Mode != ApprovalModes.Any) continue;
            var arrived = g.ArmOf(r.NodeId!, r.InEdgeId!);
            foreach (var e in g.Out(g.ForkOf(r.NodeId!).Id).Where(e => e.Id != arrived))
                roots.Add(ApprovalGraphRuntime.Child(Key(r), e.Id));
        }
        return roots;
    }

    /// <summary>J4：這個匯合在外層段 outerKey 已到達的分支（ArmOf 去重後；closed 的到達列不算）。</summary>
    public static IReadOnlySet<string> ArrivedArms(ApprovalGraphRuntime g, IReadOnlyList<ApprovalStageRun> runs, string joinId, string outerKey) =>
        runs.Where(r => r.NodeKind == ApprovalNodeKinds.Join && r.NodeId == joinId && Key(r) == outerKey && r.Status != ApprovalStageStatus.Closed)
            .Select(r => g.ArmOf(joinId, r.InEdgeId!)).ToHashSet();

    /// <summary>還在走的執行線：active、waiting、queued。</summary>
    public static IEnumerable<ApprovalStageRun> Live(IEnumerable<ApprovalStageRun> runs) =>
        runs.Where(r => r.Status is ApprovalStageStatus.Active or ApprovalStageStatus.Waiting or ApprovalStageStatus.Queued);

    /// <summary>§8.5：每個 active 的簽核節點（含加簽）都還沒有 approved 或 rejected 的任務。不讀圖（U11：結構壞掉也能撤回）。</summary>
    public static bool CanWithdraw(IReadOnlyList<ApprovalStageRun> runs, IReadOnlyList<ApprovalTask> tasks)
    {
        var active = runs.Where(r => r.Status == ApprovalStageStatus.Active && r.NodeKind is null or ApprovalNodeKinds.Approval).Select(r => r.Id).ToHashSet();
        return !tasks.Any(t => active.Contains(t.StageRunId) && t.Status is ApprovalTaskStatus.Approved or ApprovalTaskStatus.Rejected);
    }
}
