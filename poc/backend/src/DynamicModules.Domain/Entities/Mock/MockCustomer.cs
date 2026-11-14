namespace DynamicModules.Domain.Entities.Mock;

/// <summary>已停用的模擬客戶表（s2d-1g 起沒有程式讀寫）；只為對齊現有 model snapshot 而保留，產生 RemoveMockCustomer migration 時一併刪除。</summary>
public class MockCustomer
{
    public string CustomerNo { get; set; } = "";
    public string Name { get; set; } = "";
    public bool DeleteFlag { get; set; }
}
