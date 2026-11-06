using System.Text.Json;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.AppService.Data;

// 應用程式套件格式（formatVersion 2）。modules 直接重用範本的 TemplateModule；
// 保留的 section（reports、dashboards、workflows、conversions、events、printTemplates）目前只能是空陣列，
// 讀進來的值不解讀，非空一律整份拒絕（fail-closed，見 AppPackageValidator）。

public record AppPackageData(
    string Format, int FormatVersion, AppManifest Manifest, List<AppGroup>? Groups, List<TemplateModule> Modules,
    List<AppFieldAccess>? FieldAccess, List<AppOptionScope>? OptionScopes,
    List<JsonElement>? Reports, List<JsonElement>? Dashboards, List<JsonElement>? Workflows,
    List<JsonElement>? Conversions, List<JsonElement>? Events, List<JsonElement>? PrintTemplates)
{
    public const string FormatName = "formwright-app";
    public const int CurrentFormatVersion = 2;

    public List<AppGroup> Groups { get; init; } = Groups ?? [];
    public List<TemplateModule> Modules { get; init; } = Modules ?? [];
    public List<AppFieldAccess> FieldAccess { get; init; } = FieldAccess ?? [];
    public List<AppOptionScope> OptionScopes { get; init; } = OptionScopes ?? [];

    /// <summary>名稱與非空的保留 section；有值就代表這份套件需要這個版本讀不懂的功能。</summary>
    public IEnumerable<string> UnsupportedSections()
    {
        if (Reports is { Count: > 0 }) yield return "reports";
        if (Dashboards is { Count: > 0 }) yield return "dashboards";
        if (Workflows is { Count: > 0 }) yield return "workflows";
        if (Conversions is { Count: > 0 }) yield return "conversions";
        if (Events is { Count: > 0 }) yield return "events";
        if (PrintTemplates is { Count: > 0 }) yield return "printTemplates";
    }
}

public record AppManifest(
    string Id, string Version, I18nText Name, I18nText? Description, string? Icon, string? Vendor, AppManifestRequires? Requires)
{
    public AppManifestRequires Requires { get; init; } = Requires ?? new AppManifestRequires(null, null);
}

public record AppManifestRequires(List<AppRequirementData>? Apps, List<string>? Entities)
{
    public List<AppRequirementData> Apps { get; init; } = Apps ?? [];
    public List<string> Entities { get; init; } = Entities ?? [];
}

public record AppRequirementData(string Id, List<string> Modules)
{
    public List<string> Modules { get; init; } = Modules ?? [];
}

/// <summary>Code 是套件內的群組代號；開啟時由管理員決定對應到哪個既有群組、新建或略過。</summary>
public record AppGroup(string Code, I18nText Name);

/// <summary>Field 是限定欄位名 &lt;模組代號&gt;.&lt;欄位代號&gt;；Group 是 groups[].code。</summary>
public record AppFieldAccess(string Field, string Group, FieldAccess Access);

/// <summary>Option 是 &lt;模組代號&gt;.&lt;選項代號&gt;。</summary>
public record AppOptionScope(string Option, List<string> Groups)
{
    public List<string> Groups { get; init; } = Groups ?? [];
}
