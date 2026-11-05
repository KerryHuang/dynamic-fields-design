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
}
