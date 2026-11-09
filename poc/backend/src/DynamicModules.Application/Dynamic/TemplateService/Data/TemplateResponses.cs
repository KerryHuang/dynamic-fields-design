using System.Text.Json.Serialization;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.TemplateService.Data;

/// <summary>從範本建立的結果：每個模組的範本代號、實際代碼與 id；warnings 一律存在（可能是空的）。Ids 只給程式內呼叫端（Demo 產生器），不輸出到 JSON。</summary>
public record InstantiateTemplateResponse(List<InstantiatedModule> Modules, List<string> Warnings, [property: JsonIgnore] TemplateIdMap? Ids = null);

public record InstantiatedModule(string Code, string Key, long Id);

/// <summary>
/// 範本代號對剛配發的 id。FieldIds、FieldKeys 的鍵是限定名 &lt;模組代號&gt;.&lt;欄位代號&gt;（FieldKeys 的值是 Field.Key）；
/// OptionIds、BlockIds 的鍵是 &lt;模組代號&gt;.&lt;代號&gt;；ModuleKeys 是模組代號對實際 Module.Key。
/// </summary>
public record TemplateIdMap(
    IReadOnlyDictionary<string, long> FieldIds, IReadOnlyDictionary<string, string> FieldKeys,
    IReadOnlyDictionary<string, long> OptionIds, IReadOnlyDictionary<string, long> BlockIds,
    IReadOnlyDictionary<string, string> ModuleKeys);

/// <summary>Source 是 builtIn 或 imported。</summary>
public record TemplateSummary(string Key, I18nText Name, I18nText? Description, string Source, List<TemplateModuleSummary> Modules);

/// <summary>DetailOf 是表頭模組的範本代號；不是明細模組時為 null。</summary>
public record TemplateModuleSummary(string Code, string Key, I18nText Name, string? DetailOf, List<TemplateFieldSummary> Fields);

/// <summary>DependsOn 是限定名 &lt;模組代號&gt;.&lt;欄位代號&gt;；前端靠 Locked、DependsOn 做勾選連動。</summary>
public record TemplateFieldSummary(string Code, I18nText Label, string Type, bool Locked, List<string> DependsOn);

/// <summary>
/// 應用程式開啟時多帶給 TemplateInstantiator 的輸入。GroupIds：套件群組代號 → 群組 id（略過的群組不在裡面）；
/// ExternalModuleKeys：「應用程式 id/模組代號」→ 實際 Module.Key；FieldAccess、OptionScopes 是套件的欄位例外與選項範圍。
/// Seeds：共用主檔的符號（&lt;主檔 id&gt;/&lt;模組代號&gt;…）預先放進對照，參照、守衛、帶值、公式、轉換、報表都經它解析；
/// Contributions：要加到主檔模組上的區塊與欄位（共用主檔設計 §4.3）。
/// </summary>
public record AppInstantiation(
    IReadOnlyDictionary<string, long> GroupIds, IReadOnlyDictionary<string, string> ExternalModuleKeys,
    IReadOnlyList<AppFieldAccess> FieldAccess, IReadOnlyList<AppOptionScope> OptionScopes,
    InstantiationSeeds? Seeds = null, IReadOnlyList<ContributionInput>? Contributions = null);

/// <summary>預先放進對照的符號；鍵的寫法同 <see cref="TemplateIdMap"/>，Modules 的值是（Module.Id, Module.Key）。</summary>
public record InstantiationSeeds(
    IReadOnlyDictionary<string, long> Fields, IReadOnlyDictionary<string, string> FieldKeys,
    IReadOnlyDictionary<string, long> Options, IReadOnlyDictionary<string, (long Id, string Key)> Modules);

/// <summary>
/// 一筆貢獻：Prefix 是主檔模組的符號 &lt;主檔 id&gt;/&lt;模組代號&gt;，欄位、選項、區塊的對照名稱都以它開頭。
/// Reattach*：同一個應用程式重新開啟時接回的孤兒（代號 → 既有的 id），接回的不另建（設計 §7 刪除第 6 點）。
/// </summary>
public record ContributionInput(
    string Prefix, string ModuleKey, string BlockCode, I18nText BlockName, IReadOnlyList<TemplateField> Fields,
    long? ReattachBlockId, IReadOnlyDictionary<string, long> ReattachFields, IReadOnlyDictionary<string, long> ReattachOptions);
