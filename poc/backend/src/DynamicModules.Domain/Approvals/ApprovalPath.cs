using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Approvals;

/// <summary>
/// 關卡位置的純函式（計畫 a3c J2）：加簽關卡不在流程定義裡、被退回後會重來，所以「上一關」與「下一個定義關卡」
/// 不能只看關卡代號，要依關卡實例的 <c>Seq</c> 走一遍，維護目前有效的路徑。
/// </summary>
public static class ApprovalPath
{
    /// <summary>
    /// 目前有效的路徑（Seq 升冪）：approved、skipped 的關卡實例加入；returned 的關卡實例不加入，而且它之後的下一個關卡實例
    /// （closed 的排隊關卡不算）是退回目標的重來——路徑先截到退回目標之前。active、queued、closed 的不在路徑上。
    /// 依賴的不變式：只有 RejectTaskHandler 的退回上一關會產生 returned，而且同一交易內接著以 Seq = max + 1 建重來
    /// （重來失敗整筆回滾）；被退回的加簽關卡另排一筆 queued 複本，Seq 在重來之後，不會插在 returned 與重來之間。
    /// 新增其他寫入 returned 的路徑，或讓別的關卡實例排在重來之前時，要一起檢查這裡。
    /// </summary>
    public static IReadOnlyList<ApprovalStageRun> Effective(IReadOnlyList<ApprovalStageRun> runs)
    {
        var path = new List<ApprovalStageRun>();
        int? cutAt = null;
        foreach (var run in runs.Where(r => r.Status != ApprovalStageStatus.Closed).OrderBy(r => r.Seq))
        {
            if (cutAt is { } cut)
            {
                path.RemoveRange(cut, path.Count - cut);
                cutAt = null;
            }
            if (run.Status == ApprovalStageStatus.Returned)
            {
                // 退回上一關的目標＝路徑上最近一個 approved；沒有（理論上不會）就不截
                var target = path.FindLastIndex(r => r.Status == ApprovalStageStatus.Approved);
                if (target >= 0) cutAt = target;
            }
            else if (run.Status is ApprovalStageStatus.Approved or ApprovalStageStatus.Skipped) path.Add(run);
        }
        return path;
    }

    /// <summary>退回上一關的目標：路徑上 current 之前最近一個 approved（skipped 不算）；沒有回 null。</summary>
    public static ApprovalStageRun? Previous(IReadOnlyList<ApprovalStageRun> runs, ApprovalStageRun current) =>
        Effective(runs.Where(r => r.Seq <= current.Seq).ToList()).LastOrDefault(r => r.Id != current.Id && r.Status == ApprovalStageStatus.Approved);

    /// <summary>下一個要評估的定義關卡索引：路徑上最後一個 StageCode 不為 null 的關卡（含 skipped）在 def 的索引 + 1；路徑沒有定義關卡回 0。</summary>
    public static int NextStageIndex(ApprovalDefinition def, IReadOnlyList<ApprovalStageRun> runs)
    {
        var last = Effective(runs).LastOrDefault(r => r.StageCode is not null);
        return last is null ? 0 : def.Stages.FindIndex(s => s.Code == last.StageCode) + 1;
    }
}
