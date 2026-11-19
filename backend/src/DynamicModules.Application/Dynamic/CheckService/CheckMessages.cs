namespace DynamicModules.Application.Dynamic.CheckService;

/// <summary>跨記錄檢查的上限訊息；上限是政策 A29（check.maxGroupsPerWrite）、A30（check.maxBackfillRecords）的生效值。</summary>
public static class CheckMessages
{
    public static string TooManyGroups(int max) => $"這次請求牽涉的檢查組數超過 {max}，請分批處理";

    public static string TooManyToBackfill(int count, int max) =>
        $"這個模組有 {count} 筆記錄，超過 {max} 筆，一次回填索引會長時間占住寫入鎖；請在低峰時段分次處理";
}
