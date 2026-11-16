namespace DynamicModules.Application.Dynamic.DemoService.Scenarios;

/// <summary>一個 Demo 情境：只用 plan.Today 與 plan.Random 規劃步驟（純函式，不讀資料庫）。</summary>
public interface IDemoScenario
{
    string Key { get; }

    /// <summary>這個情境專用的 Random 種子（已定決策 4）。</summary>
    int Seed { get; }

    void Plan(DemoPlan plan);
}
