using System.Text.Json;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.AppService.Data;

// 應用程式套件格式（formatVersion 2 到 4）。modules 直接重用範本的 TemplateModule；
// 保留的 section（dashboards）目前只能是空陣列，讀進來的值不解讀，非空一律整份拒絕（fail-closed，見 AppPackageValidator）。

public record AppPackageData(
    string Format, int FormatVersion, AppManifest Manifest, List<AppGroup>? Groups, List<TemplateModule> Modules,
    List<AppFieldAccess>? FieldAccess, List<AppOptionScope>? OptionScopes,
    List<AppPackageReport>? Reports, List<JsonElement>? Dashboards, List<AppWorkflow>? Workflows,
    List<AppConversion>? Conversions, List<AppEvent>? Events, List<AppPrintTemplate>? PrintTemplates,
    List<AppCheck>? Checks = null, List<AppLedger>? Ledgers = null)
{
    public const string FormatName = "formwright-app";
    /// <summary>
    /// 3：workflows、conversions、reports、printTemplates 與 config.fill 可用（2 的檔案這些必須是空的）。
    /// 4：events、checks、ledgers 可用（3 以下的檔案這些必須是空的）。
    /// 5：integrations 與 config.refGuard 可用（4 以下的檔案這些必須是空的／不存在）。
    /// </summary>
    public const int CurrentFormatVersion = 5;
    public const int MinFormatVersion = 2;

    public List<AppGroup> Groups { get; init; } = Groups ?? [];
    public List<TemplateModule> Modules { get; init; } = Modules ?? [];
    public List<AppFieldAccess> FieldAccess { get; init; } = FieldAccess ?? [];
    public List<AppOptionScope> OptionScopes { get; init; } = OptionScopes ?? [];
    public List<AppPackageReport> Reports { get; init; } = Reports ?? [];
    public List<AppWorkflow> Workflows { get; init; } = Workflows ?? [];
    public List<AppConversion> Conversions { get; init; } = Conversions ?? [];
    public List<AppPrintTemplate> PrintTemplates { get; init; } = PrintTemplates ?? [];
    public List<AppEvent> Events { get; init; } = Events ?? [];
    public List<AppCheck> Checks { get; init; } = Checks ?? [];
    public List<AppLedger> Ledgers { get; init; } = Ledgers ?? [];

    /// <summary>名稱與非空的保留 section；有值就代表這份套件需要這個版本讀不懂的功能。</summary>
    public IEnumerable<string> UnsupportedSections()
    {
        if (Dashboards is { Count: > 0 }) yield return "dashboards";
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

// ---- 格式 3 的 section。全部用代號：模組代號、限定欄位名 <模組代號>.<欄位代號>、模組內唯一的選項代號、群組代號。 ----

/// <summary>一模組一個狀態機。StatusField 是本模組的欄位代號；Initial 與各狀態的 Option 是該欄位的選項代號。</summary>
public record AppWorkflow(
    string Module, string StatusField, string Initial, List<AppWorkflowState> States, List<AppWorkflowTransition> Transitions)
{
    public List<AppWorkflowState> States { get; init; } = States ?? [];
    public List<AppWorkflowTransition> Transitions { get; init; } = Transitions ?? [];
}

public record AppWorkflowState(string Option, bool ReadOnly = false);

/// <summary>Groups 是 groups[].code；空陣列代表有該模組 Edit 權限的人都可以。</summary>
public record AppWorkflowTransition(string From, string To, List<string> Groups)
{
    public List<string> Groups { get; init; } = Groups ?? [];
}

/// <summary>
/// 單據轉換。Code 就是 Conversion.Key（全站唯一，慣例以應用程式 id 當前綴）。
/// SourceStates 是來源模組狀態欄位的選項代號；Header.SourceField 是目標表頭上的追溯欄位（限定名）。
/// </summary>
public record AppConversion(
    string Code, I18nText Name, bool Active, string Source, string Target, List<string> SourceStates,
    AppConversionHeader Header, AppConversionLines? Lines)
{
    public List<string> SourceStates { get; init; } = SourceStates ?? [];
}

public record AppConversionHeader(string SourceField, List<AppCopy> Copy)
{
    public List<AppCopy> Copy { get; init; } = Copy ?? [];
}

public record AppConversionLines(string SourceModule, string TargetModule, string SourceLineField, AppCopy Quantity, List<AppCopy> Copy)
{
    public List<AppCopy> Copy { get; init; } = Copy ?? [];
}

/// <summary>
/// 列印範本。Module 是模組代號；Body 的佔位符用<b>欄位代號</b>（不是資料庫鍵）：表頭欄位 <c>{{amount}}</c>，
/// <c>{{#each 明細模組代號}}…{{/each}}</c> 裡的 <c>{{item}}</c> 指明細模組的欄位。系統值 <c>_title</c>、<c>_printedAt</c>、<c>_index</c> 照舊。
/// </summary>
public record AppPrintTemplate(string Module, string Code, I18nText Name, bool Active, string Body);

/// <summary>From、To 都是限定欄位名。</summary>
public record AppCopy(string From, string To);

/// <summary>報表。Name 不分語系（同 Report.Name）；ShareWith 是 groups[].code。</summary>
public record AppPackageReport(string Code, string Name, List<string> ShareWith, AppReportDefinition Definition)
{
    public List<string> ShareWith { get; init; } = ShareWith ?? [];
}

/// <summary>ReportDefinition 的代號版：模組用模組代號、欄位用限定欄位名、選項值寫 opt_&lt;選項代號&gt;。</summary>
public record AppReportDefinition(
    int Version, string MainModule, AppReportChild? Child,
    List<AppReportFieldRef> Columns, List<AppReportGroup> Groups, List<AppReportMeasure> Measures,
    List<AppReportFilter> Filters, AppReportFieldRef? PrimaryDateField, DynamicModules.Domain.Reports.ReportDateRange? DateRange,
    List<AppReportSort> Sort, string Viz)
{
    public List<AppReportFieldRef> Columns { get; init; } = Columns ?? [];
    public List<AppReportGroup> Groups { get; init; } = Groups ?? [];
    public List<AppReportMeasure> Measures { get; init; } = Measures ?? [];
    public List<AppReportFilter> Filters { get; init; } = Filters ?? [];
    public List<AppReportSort> Sort { get; init; } = Sort ?? [];
}

public record AppReportChild(string Module, string LinkField);

/// <summary>Field 是限定欄位名；沿參照帶出標題時為 null。Via 是來源模組上的參照欄位（限定名）。</summary>
public record AppReportFieldRef(string Source, string? Field, string? Via);

public record AppReportGroup(AppReportFieldRef Field, string? Bucket);

public record AppReportMeasure(string Id, string Kind, AppReportFieldRef? Field, AppReportFilter? Condition);

public record AppReportFilter(AppReportFieldRef Field, string Op, JsonElement? Value);

/// <summary>Key 是量值 Id，或 {source}:{field}:{via}（field、via 為限定欄位名，空的留空字串）。</summary>
public record AppReportSort(string Key, bool Desc);

// ---- 格式 4 的 section：連動事件、跨記錄檢查、分錄。引用規則同格式 3：模組代號、限定欄位名、模組內唯一的選項代號。 ----

/// <summary>
/// 連動事件。Code 就是 ModuleEvent.Key（全站唯一，慣例以應用程式 id 當前綴）；Source 是來源模組代號。
/// When 是規則條件 JSON，欄位引用寫限定欄位名，選項值寫 opt_&lt;選項代號&gt;。
/// </summary>
public record AppEvent(string Code, I18nText Name, bool Active, string Source, AppEventTrigger Trigger, JsonElement? When, List<AppEffect> Effects)
{
    public List<AppEffect> Effects { get; init; } = Effects ?? [];
}

/// <summary>From、To 是來源模組狀態機的狀態（選項代號）；Watch 是來源模組的限定欄位名。</summary>
public record AppEventTrigger(string On, List<string>? From, string? To, List<string>? Watch);

/// <summary>
/// Target、Each 是模組代號；Via 是來源模組上的限定欄位名；OfEvent 是同一份套件的事件代號。
/// 自訂效果（kind = custom）的參數是外掛專屬的，不轉成代號，所以套件不收。
/// </summary>
public record AppEffect(string Code, string Kind, string? Target, string? Each, string? Via, List<AppEventSet>? Set, string? OfEvent, string? OfEffect)
{
    public List<AppEventSet> Set { get; init; } = Set ?? [];
}

/// <summary>To 是目標模組的限定欄位名；From／Formula／Const 三擇一。公式裡的 {欄位} 寫成 {&lt;模組代號&gt;.&lt;欄位代號&gt;}。</summary>
public record AppEventSet(string To, AppEventFrom? From, AppEventFormula? Formula, JsonElement? Const, bool AllowHiddenSource = false);

public record AppEventFrom(string Side, string Field);

public record AppEventFormula(string Side, string Expr);

/// <summary>跨記錄檢查。Code 就是 ModuleCheck.Key；Module 是被檢查的模組代號，GroupBy、Field、Limit 的欄位都是限定欄位名。</summary>
public record AppCheck(
    string Code, I18nText Name, bool Active, string Module, List<string> GroupBy, string Function, string? Field,
    JsonElement? Where, AppCheckAssert Assert, I18nText Message)
{
    public List<string> GroupBy { get; init; } = GroupBy ?? [];
}

public record AppCheckAssert(string Op, AppCheckLimit Limit);

public record AppCheckLimit(decimal? Const, string? Field, string? Via);

/// <summary>分錄設定。ReversalField、Negate、Swap 都是限定欄位名。</summary>
public record AppLedger(string Module, string ReversalField, List<string> Negate, List<List<string>> Swap)
{
    public List<string> Negate { get; init; } = Negate ?? [];
    public List<List<string>> Swap { get; init; } = Swap ?? [];
}
