namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>站台層級的鍵值設定。目前只有 Demo 產生器寫入的 <c>demo.generatedOn</c>（yyyy-MM-dd）。</summary>
public class SystemSetting
{
    public const string DemoGeneratedOn = "demo.generatedOn";

    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
