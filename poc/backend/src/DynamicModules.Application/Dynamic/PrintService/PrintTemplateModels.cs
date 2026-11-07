using System.Text.RegularExpressions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.PrintService;

public record PrintTemplateInput(string Key, I18nText Name, string Body, bool Active);

/// <summary>設計器看到的完整定義（含內文）。</summary>
public record PrintTemplateResponse(string Key, I18nText Name, string Body, bool Active);

/// <summary>一般使用者看到的清單項目：只有鍵與名稱，不含範本內文。</summary>
public record PrintTemplateOption(string Key, I18nText Name);

public record PrintedDocument(string Html);

public static partial class PrintTemplateRules
{
    public const int MaxBodyLength = 100_000;
    public const string NotFound = "找不到列印範本";

    [GeneratedRegex(@"^[a-z][a-z0-9-]{1,39}$")]
    public static partial Regex KeyPattern();

    public static PrintTemplateResponse ToResponse(this PrintTemplate t) => new(t.Key, t.NameI18n, t.Body, t.Active);
}
