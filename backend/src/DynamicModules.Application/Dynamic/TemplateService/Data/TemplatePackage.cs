using System.Text.Json;
using System.Text.Json.Serialization;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.TemplateService.Data;

// 第 8 階段決策 1 的範本格式（formatVersion 1）。範本內以代號互相引用，套用時才配發 id。
// 集合屬性在 JSON 缺漏時視為空清單；Config 缺漏時視為空物件。

public record TemplatePackage(
    int FormatVersion, string Key, I18nText Name, I18nText? Description,
    List<TemplateModule> Modules, List<string>? Warnings)
{
    public List<TemplateModule> Modules { get; init; } = Modules ?? [];
}

/// <summary>
/// Section 是選單分區：JSON 可以是字串（只有繁中）或多語系物件，見 <see cref="SectionTextJsonConverter"/>。
/// TitleField 只有擴充模組可以是 null（標題取自既有資料）。
/// TitleTemplate 是標題樣板，例如 <c>{no} {name}</c>，{} 內是本模組的欄位代號；有值時記錄標題照樣板組成。
/// Extensible 只有主檔套件的模組可以設（接受其他應用程式貢獻欄位）。兩者都是應用程式套件格式 6 才有。
/// TasksEnabled 只有 false 有意義，省略＝開；應用程式套件格式 7 才有，模組範本也適用（任務設計 §4.4）。
/// </summary>
public record TemplateModule(
    string Code, string Key, I18nText Name, string? Icon,
    [property: JsonConverter(typeof(SectionTextJsonConverter))] I18nText? Section, ModuleKind Kind,
    string? HostEntityKey, string? TitleField, List<TemplateBlock> Blocks, List<TemplateRule> Rules,
    List<TemplateView> Views, List<TemplatePermission> Permissions, bool? Extensible = null, string? TitleTemplate = null,
    bool? TasksEnabled = null)
{
    public List<TemplateBlock> Blocks { get; init; } = Blocks ?? [];
    public List<TemplateRule> Rules { get; init; } = Rules ?? [];
    public List<TemplateView> Views { get; init; } = Views ?? [];
    public List<TemplatePermission> Permissions { get; init; } = Permissions ?? [];
}

/// <summary>Columns 在 JSON 缺漏時用 <see cref="Block.DefaultColumns"/>。</summary>
public record TemplateBlock(string Code, I18nText Label, bool Collapsed, List<TemplateField> Fields, int Columns = Block.DefaultColumns)
{
    public List<TemplateField> Fields { get; init; } = Fields ?? [];
}

/// <summary>
/// Config 是 FieldConfig 的 JSON，但 formula、選項、rollup、parentDefaultField 用代號；替換在套用時做。
/// Active 是 false 代表客戶停用了這個欄位（應用程式套件格式 6，只用在主檔模組與貢獻欄位）；省略時為啟用。
/// </summary>
public record TemplateField(string Code, FieldType Type, I18nText Label, JsonElement Config, List<TemplateOption> Options, bool? Active = null)
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    public JsonElement Config { get; init; } = Config.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? EmptyObject : Config;
    public List<TemplateOption> Options { get; init; } = Options ?? [];
}

public record TemplateOption(string Code, I18nText Label, string? Color);

/// <summary>Targets 是 field:代號、block:代號 或 *；Options 是選項代號（選項限制用）。</summary>
public record TemplateRule(RuleKind Kind, List<string> Targets, JsonElement Condition, List<string> Options, I18nText? Message, bool Active)
{
    public List<string> Targets { get; init; } = Targets ?? [];
    public List<string> Options { get; init; } = Options ?? [];
}

public record TemplateView(I18nText Name, List<string> Columns, JsonElement? Filter, List<TemplateSort> Sort, bool IsDefault)
{
    public List<string> Columns { get; init; } = Columns ?? [];
    public List<TemplateSort> Sort { get; init; } = Sort ?? [];
}

public record TemplateSort(string Field, string Order);

/// <summary>Group 是權限群組的 Key（如 sales）。</summary>
public record TemplatePermission(string Group, PrivilegesDto Privileges, DataScope? Scope);
