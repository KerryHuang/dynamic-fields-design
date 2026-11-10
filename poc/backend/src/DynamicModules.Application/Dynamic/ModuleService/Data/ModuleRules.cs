using DynamicModules.Domain.ValueObjects;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.ModuleService.Data;

public static class ModuleRules
{
    /// <summary>模組代碼（Module.Key）的格式；範本的 key 與模組 code、key 共用。</summary>
    public const string ModuleKeyPattern = "^[a-z][a-z0-9-]{1,39}$";

    public static IRuleBuilderOptions<T, string> ValidModuleKey<T>(this IRuleBuilder<T, string> rule) =>
        rule.Matches(ModuleKeyPattern)
            .WithMessage("模組代碼只能用小寫英文、數字、連字號，2 到 40 字，英文開頭");

    public static IRuleBuilderOptions<T, I18nText> ValidModuleName<T>(this IRuleBuilder<T, I18nText> rule) =>
        rule.Must(n => n is not null && n.IsValid(out _)).WithMessage("名稱必須有繁體中文");

    public const int SectionMaxLength = 100;

    /// <summary>選單分區：可省略；語系鍵同 <see cref="I18nText.IsValid"/>；每個語系值不超過 100 字。繁中空白不算錯，視同不分區。</summary>
    public static IRuleBuilderOptions<T, I18nText?> ValidModuleSection<T>(this IRuleBuilder<T, I18nText?> rule) =>
        rule.Must(s => UnsupportedLocale(s) is null)
            .WithMessage((_, s) => $"不支援的語系：{UnsupportedLocale(s)}")
            .Must(s => s is null || s.Values.Values.All(v => (v?.Length ?? 0) <= SectionMaxLength))
            .WithMessage($"選單分區不能超過 {SectionMaxLength} 字");

    private static string? UnsupportedLocale(I18nText? s) =>
        s?.Values.Keys.FirstOrDefault(k => !I18nText.SupportedLocales.Contains(k));
}
