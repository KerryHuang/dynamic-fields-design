using DynamicModules.Application.Dynamic.RecordService.Data;

namespace DynamicModules.Application.Dynamic.DemoService;

/// <summary>
/// 規劃時建立、執行時回填的一筆記錄：Id、RowVersion、明細列由 create／update 的回應填入，
/// 後面的步驟（含其他情境）靠它取得最新的 Id 與 RowVersion。
/// </summary>
public sealed class DemoRecord(string moduleCode, DateOnly date)
{
    /// <summary>範本的模組代號。</summary>
    public string ModuleCode { get; } = moduleCode;

    /// <summary>規劃時的主日期（訂單日期、報修日期），給其他情境挑選用。</summary>
    public DateOnly Date { get; } = date;

    /// <summary>規劃時決定、其他情境要讀的值（狀態、交期等）。</summary>
    public Dictionary<string, object?> Facts { get; } = [];

    public long Id { get; set; }

    public string RowVersion { get; set; } = "";

    /// <summary>鍵是明細 Module.Key；列依回應 Details 的順序。</summary>
    public Dictionary<string, List<(long Id, string RowVersion)>> DetailRows { get; } = [];

    public void Fill(DynamicRecordResponse response)
    {
        Id = response.Id;
        RowVersion = response.RowVersion;
        if (response.Details is null) return;
        foreach (var (key, rows) in response.Details)
            DetailRows[key] = rows.Select(r => (r.Id, r.RowVersion)).ToList();
    }
}
