namespace DynamicModules.Domain.Entities.Mock;

/// <summary>代表外部系統既有的客戶表（POC 用 mock）。</summary>
public class MockCustomer
{
    public string CustomerNo { get; set; } = "";
    public string Name { get; set; } = "";
    public bool DeleteFlag { get; set; }
}
