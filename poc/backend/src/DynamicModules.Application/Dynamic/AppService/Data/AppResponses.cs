using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.AppService.Data;

/// <summary>
/// 目錄裡的一個套件；Status 是它對應的已開啟應用程式狀態（Open、Closed），沒開過是 null。
/// Merged、Warnings 只在匯入時有意義：再匯入已存在的共用主檔是合併（共用主檔設計 §9），Warnings 列出沒合併、避撞與保留既有的項目。
/// </summary>
public record AppPackageSummary(
    string Id, string Version, I18nText Name, I18nText? Description, string? Icon, string? Vendor, string Source,
    AppManifestRequires Requires, List<AppGroup> Groups, List<TemplateModuleSummary> Modules, string? Status)
{
    public bool Merged { get; init; }
    public List<string> Warnings { get; init; } = [];
}

public record AppModuleResponse(string Code, string Key, long Id, I18nText Name, string Status);

/// <summary>Dependents：目前依賴它的、開啟中的應用程式；UnaffiliatedReferrers：不屬於任何應用程式、卻有欄位參照它的模組（只警告）。</summary>
public record AppResponse(
    string Key, I18nText Name, string? Icon, string PackageVersion, int FormatVersion, string Source, string Status,
    string RowVersion, AppRequires Requires, List<AppModuleResponse> Modules,
    List<string> Dependents, List<string> UnaffiliatedReferrers, List<string> Warnings,
    List<IntegrationResponse>? Integrations = null)
{
    public List<IntegrationResponse> Integrations { get; init; } = Integrations ?? [];
    /// <summary>app 或 master（共用主檔，由使用它的應用程式管理，不能直接開關）。</summary>
    public string Kind { get; init; } = AppManifest.AppKindName;
    /// <summary>只在開啟回應有：開啟時被略過的群組由備援頂替的每一個來源（簽核設計 §14.4）；同樣的內容也逐條列在 Warnings。</summary>
    public IReadOnlyList<AppApprovalFlowCodec.Substitution> ApprovalSubstitutions { get; init; } = [];
}

/// <summary>
/// 整合的現況。Status：Disabled、Waiting、Active、Suspended、Held、Blocked；Reason 說明原因（人看的）。
/// Partners 是夥伴應用程式 id 與用到的模組。
/// </summary>
public record IntegrationResponse(
    string Code, I18nText Name, bool Enabled, bool Installed, string Status, string? Reason, string OnPartnerClosed, List<AppRequirementData> Partners);

/// <summary>關閉的結果：Closed 是這次實際關閉的應用程式（含連帶關閉的依賴者，依賴者在前）。</summary>
public record CloseAppResponse(AppResponse App, List<string> Closed, List<string> Warnings);

public record KeyConflict(string ModuleCode, string Key);

/// <summary>Reason：notInstalled、closed、moduleMissing、moduleNotActive、entityMissing、masterNotAvailable（Kind = master，目錄與匯入套件都沒有這份主檔）。</summary>
public record MissingDependency(string Kind, string? AppId, string? ModuleCode, string? Entity, string Reason);

public record GroupSuggestion(string Code, I18nText Name, string Suggestion, long? SuggestedGroupId);

/// <summary>
/// 開啟前的檢查結果；全部為空才能開啟。Masters：會一起處理的共用主檔（依相依順序）；
/// Unbound：主檔已開啟但被客戶改過，套件引用的符號對不上（共用主檔 §4.6），整批 409。
/// </summary>
public record OpenPreview(
    IReadOnlyList<string> Errors, IReadOnlyList<KeyConflict> KeyConflicts, IReadOnlyList<string> HostEntityConflicts,
    IReadOnlyList<MissingDependency> MissingDependencies, IReadOnlyList<GroupSuggestion> Groups, IReadOnlyList<string> Warnings,
    IReadOnlyList<MasterPreview>? Masters = null, IReadOnlyList<string>? Unbound = null)
{
    public IReadOnlyList<MasterPreview> Masters { get; init; } = Masters ?? [];
    /// <summary>簽核流程引用的群組被略過、改由備援頂替的每一個來源（簽核設計 §14.4）；每一筆另有一條警告。只是提示，不擋開啟。</summary>
    public IReadOnlyList<AppApprovalFlowCodec.Substitution> ApprovalSubstitutions { get; init; } = [];
    public IReadOnlyList<string> Unbound { get; init; } = Unbound ?? [];
    public bool CanOpen => Errors.Count == 0 && KeyConflicts.Count == 0 && HostEntityConflicts.Count == 0 && MissingDependencies.Count == 0
                           && Unbound.Count == 0;
}

/// <summary>Action：open（還沒有，自動開啟）、bind（已開啟，直接綁定）、reopen（已關閉，重新開啟後綁定）。Groups 只在 open 時有。</summary>
public record MasterPreview(string Id, I18nText Name, string Action, IReadOnlyList<GroupSuggestion> Groups);

/// <summary>
/// 開啟請求的內容。ModuleKeys：模組代號 → 新的 Module.Key；GroupMap：群組代號 → 對應方式；
/// Locale：建立群組時用的語系，預設 zh-TW；Masters：要自動開啟的共用主檔 id → 它的群組對應（沒列的群組略過，同 GroupMap）。
/// </summary>
public record OpenAppBody(
    Dictionary<string, string>? ModuleKeys, List<string>? ExcludeFields, Dictionary<string, GroupMapEntry>? GroupMap, string? Locale,
    Dictionary<string, Dictionary<string, GroupMapEntry>>? Masters = null)
{
    public Dictionary<string, Dictionary<string, GroupMapEntry>> Masters { get; init; } = Masters ?? [];
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
    public const string MasterManaged = "這是共用主檔，由使用它的應用程式管理，不能直接開啟、關閉或重新開啟";
    public const string MasterNotAvailable = "masterNotAvailable";
    public const string MasterInUse = "共用主檔還有應用程式在使用，不能刪除";

    public static string FlowCodeTaken(string code) => $"approvalFlows.{code}: 流程代碼已被使用";

    public static string FlowFieldExcluded(string field, string flow) => $"excludeFields: 「{field}」是簽核流程「{flow}」需要的欄位，不能排除";

    /// <summary>a5a1 期間的暫時限制（計畫 J9），a5a2 換成真正的格式 8 匯出時刪除。</summary>
    public static string ExportBlockedByApproval(string app) => $"應用程式「{app}」有簽核流程，匯出要等格式 8 的簽核流程匯出完成";

    /// <summary>開啟預覽的頂替警告（繁中），與 <see cref="AppApprovalFlowCodec.Substitution"/> 一一對應。</summary>
    public static string ApprovalSubstituted(AppApprovalFlowCodec.Substitution s)
    {
        string Zh(I18nText t) => t.Resolve(I18nText.FallbackLocale);
        var where = s.Kind switch
        {
            AppApprovalFlowCodec.KindApprovers => $"關卡「{Zh(s.StageName!)}」的簽核人來源",
            AppApprovalFlowCodec.KindCandidates => $"關卡「{Zh(s.StageName!)}」的候選群組",
            _ => "的備援簽核人"
        };
        var by = s.ReplacedBy == AppApprovalFlowCodec.ByFlowFallback ? "流程的備援簽核人" : "預設備援簽核人（簽核政策）";
        return $"簽核流程「{Zh(s.FlowName)}」{where}「{Zh(s.GroupName)}」已略過，改由{by}頂替";
    }
}

public static class AppMapper
{
    public static string StatusName(AppStatus s) => s.ToString();
}
