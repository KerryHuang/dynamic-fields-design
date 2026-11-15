using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Approvals;

/// <summary>路線比較的結果：Changed＝路線不同；UndecidableNodeId 非 null＝這個條件節點的出線無法判斷（呼叫端回 409，此時 Changed 一律 false）。</summary>
public sealed record GraphRouteCheck(bool Changed, string? UndecidableNodeId);

/// <summary>
/// graph 實例每次同意的路線比較（流程圖設計 §8.7 第 2–4 點；計畫 fg2 J13、J27）。純函式，只看圖與節點實例。
/// 流程與版本是否相同由呼叫端先判斷（換了就走第一版的 Changed，不呼叫這裡）。
/// </summary>
public static class ApprovalGraphRoute
{
    // 與 Application 的 ApprovalSkipReasons.Condition 同值；Domain 不能引用 Application，所以在這裡寫一次。
    private const string SkipByCondition = "condition";

    /// <summary>
    /// 同一流程與版本時的路線比較：各段有效路徑上 skipped(condition) 的簽核節點，略過條件重算變成立 → Changed；
    /// 有效路徑上 passed 的條件節點，重算選中的出線與當時不同 → Changed（當時的出線＝之後第一列 InEdgeId 屬於它出線的節點實例）；
    /// 被取消的分支段（<see cref="ApprovalGraphPath.CancelledRoots"/>）不參與；條件節點無法判斷回 UndecidableNodeId，而且比 Changed 優先（fail closed）。
    /// eval 只有一個（ApprovalGraphMode.Eval，J27），「無法判斷」依條件種類分流：條件節點的出線交給 Choose，Undecidable 就回 UndecidableNodeId；
    /// skipped(condition) 簽核節點的略過條件 eval != Fails 就算成立 → Changed（同第一版 ApprovalRouter.Holds：解析不了視為成立，寧可多簽）。
    /// g 必須從實例自己綁定的快照建（CancelledRoots 對圖外的代號會丟例外）。
    /// </summary>
    public static GraphRouteCheck Changed(ApprovalGraphRuntime g, IReadOnlyList<ApprovalStageRun> runs, Func<JsonElement, ConditionTruth> eval)
    {
        var cancelled = ApprovalGraphPath.CancelledRoots(g, runs);
        var live = runs.Where(r => !cancelled.Any(root => ApprovalGraphRuntime.Within(r.BranchKey ?? "", root))).ToList();
        // 各段有效路徑（退回截斷後只剩最新一次），依 Seq 排；條件節點與簽核節點的檢查都只看這條路徑上的列
        var path = live.Select(r => r.BranchKey ?? "").Distinct()
            .SelectMany(key => ApprovalGraphPath.Segment(live, key)).OrderBy(r => r.Seq).ToList();

        var changed = false;
        foreach (var run in path)
        {
            if (run.NodeKind == ApprovalNodeKinds.Condition && run.Status == ApprovalStageStatus.Passed)
            {
                var (now, undecidable) = g.Choose(run.NodeId!, eval);
                if (undecidable) return new(false, run.NodeId);
                // 當時走的出線：這個條件節點之後第一列、進線屬於它出線的節點實例（可能是外層段的匯合到達列，所以不限同段）
                var outs = g.Out(run.NodeId!).Select(e => e.Id).ToHashSet();
                var then = runs.Where(r => r.Seq > run.Seq && r.InEdgeId is not null && outs.Contains(r.InEdgeId)).OrderBy(r => r.Seq).FirstOrDefault();
                if (then is not null && then.InEdgeId != now!.Id) changed = true;
            }
            else if (run.Status == ApprovalStageStatus.Skipped && run.SkipReason == SkipByCondition
                && g.Node(run.NodeId!).When is { } when && eval(when) != ConditionTruth.Fails)
                changed = true;
        }
        return new(changed, null);
    }
}
