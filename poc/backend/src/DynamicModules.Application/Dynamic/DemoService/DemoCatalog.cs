namespace DynamicModules.Application.Dynamic.DemoService;

/// <summary>Demo 資料用到的內建範本與模組（固定順序）。</summary>
public static class DemoCatalog
{
    /// <summary>套用範本的順序。</summary>
    public static IReadOnlyList<string> TemplateKeys { get; } = ["order", "repair", "mold-care", "quality"];

    /// <summary>啟用模組的順序（Module.Key）：被參照、被彙總的模組先啟用。</summary>
    public static IReadOnlyList<string> ActivationOrder { get; } =
        ["order", "order-line", "repair", "mold-info", "customer-info", "mold-care", "mold-care-part", "quality"];
}
