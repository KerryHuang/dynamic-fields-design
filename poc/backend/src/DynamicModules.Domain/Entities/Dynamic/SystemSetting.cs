namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>站台層級的鍵值設定。Demo 產生器寫入的 <c>demo.generatedOn</c>（yyyy-MM-dd），以及全站外觀預設 <c>theme.defaultMode</c>、<c>theme.defaultStyle</c>（缺鍵代表出廠預設）。</summary>
public class SystemSetting
{
    public const string DemoGeneratedOn = "demo.generatedOn";
    public const string ThemeDefaultMode = "theme.defaultMode";
    public const string ThemeDefaultStyle = "theme.defaultStyle";

    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
