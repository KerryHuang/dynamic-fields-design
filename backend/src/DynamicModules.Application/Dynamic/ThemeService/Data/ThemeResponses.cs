using System.Text.Json.Serialization;

namespace DynamicModules.Application.Dynamic.ThemeService.Data;

public record ThemeDefaultsResponse(string Mode, string Style);

/// <summary>Mode、Style 是個人設定，null 代表跟隨全站預設；Program 全域略過 null，這裡要明確保留。</summary>
public record MeThemeResponse(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Mode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Style,
    string EffectiveMode, string EffectiveStyle, string DefaultMode, string DefaultStyle, bool CanSave);
