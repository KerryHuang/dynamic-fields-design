using System.Text.Json;
using System.Text.Json.Serialization;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.SchemaService.Data;

/// <summary>Ledger：不可修改的分錄模組，修改與刪除權限同時被壓成 false。ReadOnly：模組所屬應用程式已關閉。Permissions 的新增、修改、刪除同時被壓成 false，前端照權限就會藏起按鈕。
/// HasTitleTemplate：標題由樣板組成，列表的標題欄叫「標題」，依標題欄位排序就是依組好的標題排序。</summary>
public record ModuleSchemaResponse(
    string Key, string ProgramCode, I18nText Name, string? Icon, string? TitleFieldKey, int Version, List<SchemaBlock> Blocks,
    SchemaPermissions Permissions, List<SchemaRule> Rules,
    ModuleKind Kind, string? HostEntityKey, List<SchemaDetail>? Details = null, SchemaWorkflow? Workflow = null, bool ReadOnly = false,
    bool Ledger = false, bool HasTitleTemplate = false)
{
    /// <summary>ETag 用的版本：表頭 Version 接上每個明細模組的 Version（依 Key 排序），以 . 串接；不序列化。</summary>
    [JsonIgnore]
    public string ETagVersion { get; init; } = Version.ToString();

    /// <summary>有效權限的雜湊，只給 ETag 用，不序列化。</summary>
    [JsonIgnore]
    public string PermHash { get; init; } = "";
}

/// <summary>狀態機：狀態值是 opt_&lt;id&gt;；Transitions 只列目前使用者能做的轉移（預覽為全部）。前端只用來縮小選項，後端另外強制。</summary>
public record SchemaWorkflow(string StatusFieldKey, string InitialState, List<SchemaWorkflowState> States, List<SchemaWorkflowTransition> Transitions);

public record SchemaWorkflowState(string Value, bool ReadOnly);

public record SchemaWorkflowTransition(string From, string To);

public record SchemaPermissions(bool Read, bool Add, bool Edit, bool Delete, bool PrintExport, bool Pricing);

/// <summary>表頭 schema 內的明細模組：欄位已攤平、排除表頭欄位，規則是明細模組自己的。</summary>
public record SchemaDetail(
    string ModuleKey, I18nText Name, long ParentFieldId, string ParentFieldKey, string? TitleFieldKey,
    List<SchemaField> Fields, List<SchemaRule> Rules);

public record SchemaBlock(long Id, I18nText Label, bool Collapsed, int Columns, List<SchemaField> Fields);

/// <param name="LockedBy">凍結的貢獻欄位（貢獻者應用程式已關閉，共用主檔設計 §7）：屬於哪個應用程式；其他欄位是 null。</param>
public record SchemaField(
    long Id, string Key, FieldType Type, I18nText Label, FieldConfig Config, List<SchemaOption> Options,
    SchemaReference? Reference = null, SchemaLock? LockedBy = null);

/// <summary>欄位凍結的原因：所屬應用程式的 id 與名稱。</summary>
public record SchemaLock(string AppKey, I18nText AppName);

/// <summary>參照欄位的目標資訊；LightUrl 相對於 API 根路徑。</summary>
public record SchemaReference(string Kind, string Key, I18nText Name, string LightUrl);

/// <summary>給前端即時預覽的規則；Options 是 opt_&lt;id&gt;，只在 OptionFilter 有值。</summary>
public record SchemaRule(RuleKind Kind, JsonElement Condition, List<string> Targets, List<string> Options, I18nText? Message);

public record SchemaOption(string Value, I18nText Label, string? Color, bool Active);

/// <summary>Closed：所屬應用程式已關閉，仍可進入查閱，前端放在「已關閉的應用程式」分組。</summary>
/// <summary>Section 是選單分區的多語系文字；前端以繁中值分組（不同應用程式的同名分區合成一組）。</summary>
public record MenuItem(string Key, I18nText Name, string? Icon, I18nText? Section, bool Closed = false);

public static class SchemaETag
{
    /// <summary>schema 的 ETag，含雙引號，如 "order-v3-1a2b3c4d"；換人或改權限 permHash 就變。</summary>
    public static string For(string key, string version, string permHash) => $"\"{key}-v{version}-{permHash}\"";
}
