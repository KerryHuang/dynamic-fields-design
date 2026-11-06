namespace DynamicModules.Domain.ValueObjects;

/// <summary>外觀設定的允許值與出廠預設；比對區分大小寫。</summary>
public static class ThemeOptions
{
    public const string FactoryMode = "system", FactoryStyle = "workbench";

    private static readonly string[] Modes = ["light", "dark", "system"];
    private static readonly string[] Styles = ["workbench", "airy", "enterprise", "industrial"];

    /// <summary>不在允許值就回 null。</summary>
    public static string? NormalizeMode(string? value) => value is not null && Modes.Contains(value) ? value : null;

    public static string? NormalizeStyle(string? value) => value is not null && Styles.Contains(value) ? value : null;
}
