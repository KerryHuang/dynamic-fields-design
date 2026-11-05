using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.TemplateService.Data;

/// <summary>從範本建立的結果：每個模組的範本代號、實際代碼與 id；warnings 一律存在（可能是空的）。</summary>
public record InstantiateTemplateResponse(List<InstantiatedModule> Modules, List<string> Warnings);

public record InstantiatedModule(string Code, string Key, long Id);

/// <summary>Source 是 builtIn 或 imported。</summary>
public record TemplateSummary(string Key, I18nText Name, I18nText? Description, string Source, List<TemplateModuleSummary> Modules);

/// <summary>DetailOf 是表頭模組的範本代號；不是明細模組時為 null。</summary>
public record TemplateModuleSummary(string Code, string Key, I18nText Name, string? DetailOf, List<TemplateFieldSummary> Fields);

/// <summary>DependsOn 是限定名 &lt;模組代號&gt;.&lt;欄位代號&gt;；前端靠 Locked、DependsOn 做勾選連動。</summary>
public record TemplateFieldSummary(string Code, I18nText Label, string Type, bool Locked, List<string> DependsOn);
