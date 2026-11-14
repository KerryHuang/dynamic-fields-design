namespace DynamicModules.Application.Dynamic.DemoService;

public static class DemoMessages
{
    public static string MasterHasRecords(string masterId, string moduleKey) => $"共用主檔 {masterId} 的模組 {moduleKey} 已有記錄";
}
