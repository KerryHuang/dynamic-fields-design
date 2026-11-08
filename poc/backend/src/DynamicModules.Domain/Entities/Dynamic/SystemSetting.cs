namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>站台層級的鍵值設定。Demo 產生器寫入的 <c>demo.generatedOn</c>（yyyy-MM-dd），以及全站外觀預設 <c>theme.defaultMode</c>、<c>theme.defaultStyle</c>（缺鍵代表出廠預設）。</summary>
public class SystemSetting
{
    public const string DemoGeneratedOn = "demo.generatedOn";
    /// <summary>索引回填（ScaledValue 精確換算、文字截斷標記）已完成的版本；未達 <c>RecordIndexer.BackfillVersion</c> 時報表不走 SQL 聚合。</summary>
    public const string IndexBackfillVersion = "index.backfillVersion";
    public const string ThemeDefaultMode = "theme.defaultMode";
    public const string ThemeDefaultStyle = "theme.defaultStyle";

    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
