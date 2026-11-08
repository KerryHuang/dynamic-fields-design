using DynamicModules.Application.Dynamic.DemoService.Scenarios;

namespace DynamicModules.Application.Dynamic.DemoService;

/// <summary>Demo 資料用到的內建應用程式與情境（固定順序）。</summary>
public static class DemoCatalog
{
    public const string MesAppId = "mes-mold";
    public const string CrmAppId = "crm-sales";
    public const string InventoryAppId = "inventory";

    /// <summary>Demo 開啟的內建應用程式（固定順序）。</summary>
    public static IReadOnlyList<string> AppIds { get; } = [MesAppId, CrmAppId, InventoryAppId];

    /// <summary>情境的規劃順序（也是同一時刻步驟的執行順序）。</summary>
    public static IReadOnlyList<IDemoScenario> Scenarios { get; } =
        [new OrderScenario(), new RepairScenario(), new MoldCareScenario(), new QualityScenario(), new CrmScenario(),
         new MesQuoteScenario(), new InventoryScenario(), new MesProductionScenario()];
}
