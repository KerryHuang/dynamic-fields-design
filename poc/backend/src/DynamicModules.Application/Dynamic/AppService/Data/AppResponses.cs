using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.AppService.Data;

/// <summary>目錄裡的一個套件；Status 是它對應的已開啟應用程式狀態（Open、Closed），沒開過是 null。</summary>
public record AppPackageSummary(
    string Id, string Version, I18nText Name, I18nText? Description, string? Icon, string? Vendor, string Source,
    AppManifestRequires Requires, List<AppGroup> Groups, List<TemplateModuleSummary> Modules, string? Status);

public record AppModuleResponse(string Code, string Key, long Id, I18nText Name, string Status);

/// <summary>Dependents：目前依賴它的、開啟中的應用程式；UnaffiliatedReferrers：不屬於任何應用程式、卻有欄位參照它的模組（只警告）。</summary>
public record AppResponse(
    string Key, I18nText Name, string? Icon, string PackageVersion, int FormatVersion, string Source, string Status,
    string RowVersion, AppRequires Requires, List<AppModuleResponse> Modules,
    List<string> Dependents, List<string> UnaffiliatedReferrers, List<string> Warnings);

/// <summary>關閉的結果：Closed 是這次實際關閉的應用程式（含連帶關閉的依賴者，依賴者在前）。</summary>
public record CloseAppResponse(AppResponse App, List<string> Closed, List<string> Warnings);

public record KeyConflict(string ModuleCode, string Key);

/// <summary>Reason：notInstalled、closed、moduleMissing、moduleNotActive、entityMissing。</summary>
public record MissingDependency(string Kind, string? AppId, string? ModuleCode, string? Entity, string Reason);

public record GroupSuggestion(string Code, I18nText Name, string Suggestion, long? SuggestedGroupId);

/// <summary>開啟前的檢查結果；全部為空才能開啟。</summary>
public record OpenPreview(
    IReadOnlyList<string> Errors, IReadOnlyList<KeyConflict> KeyConflicts, IReadOnlyList<string> HostEntityConflicts,
    IReadOnlyList<MissingDependency> MissingDependencies, IReadOnlyList<GroupSuggestion> Groups, IReadOnlyList<string> Warnings)
{
    public bool CanOpen => Errors.Count == 0 && KeyConflicts.Count == 0 && HostEntityConflicts.Count == 0 && MissingDependencies.Count == 0;
}

/// <summary>
/// 開啟請求的內容。ModuleKeys：模組代號 → 新的 Module.Key；GroupMap：群組代號 → 對應方式；
/// Locale：建立群組時用的語系，預設 zh-TW。
/// </summary>
public record OpenAppBody(
    Dictionary<string, string>? ModuleKeys, List<string>? ExcludeFields, Dictionary<string, GroupMapEntry>? GroupMap, string? Locale)
{
    public Dictionary<string, string> ModuleKeys { get; init; } = ModuleKeys ?? [];
    public List<string> ExcludeFields { get; init; } = ExcludeFields ?? [];
    public Dictionary<string, GroupMapEntry> GroupMap { get; init; } = GroupMap ?? [];
    public string Locale { get; init; } = Locale ?? I18nText.FallbackLocale;
}

/// <summary>Mode：map（GroupId 必填）、create、skip。</summary>
public record GroupMapEntry(string Mode, long? GroupId);

public static class AppMessages
{
    public const string NotFound = "應用程式不存在";
    public const string PackageNotFound = "套件不存在";
    public const string AlreadyOpen = "應用程式已開啟";
    public const string AlreadyClosedHint = "應用程式已開啟但目前是關閉狀態，請改用重新開啟";
}

public static class AppMapper
{
    public static string StatusName(AppStatus s) => s.ToString();
}
