namespace DynamicModules.Domain.Entities.Mock;

/// <summary>代表外部系統既有的模具表（POC 用 mock）。</summary>
public class MockMold
{
    public string MoldNo { get; set; } = "";
    public string Name { get; set; } = "";
    public bool DeleteFlag { get; set; }
}
