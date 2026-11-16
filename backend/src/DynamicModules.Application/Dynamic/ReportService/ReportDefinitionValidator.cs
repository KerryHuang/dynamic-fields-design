using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.ReportService;

/// <summary>存檔與預覽共用的驗證流程：名稱、結構、模組授權、欄位與條件；通過時回傳條件值正規化後的定義。</summary>
public class ReportDefinitionValidator(ReportModuleLoader loader, IModuleRepository modules, FieldTypeRegistry registry)
{
    /// <param name="name">null 表示不檢查名稱（預覽）。</param>
    public async Task<(ReportDefinition? Definition, ServiceResult? Failure)> ValidateAsync(string? name, ReportDefinition definition, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (name is not null && ReportRules.CheckName(name) is { } nameError) errors["name"] = [nameError];
        var shape = ReportRules.CheckShape(definition);
        if (shape.Count > 0) errors["definition"] = shape.ToArray();
        if (errors.Count > 0) return (null, ServiceResult.Invalid(errors));

        var (set, denied) = await loader.LoadAsync(definition, PermissionAction.Read, ct);
        if (set is null) return (null, denied);
        var (checkErrors, normalized) = ReportRules.Check(definition, set, registry, await modules.ListUserIdsAsync(ct));
        return normalized is null
            ? (null, ServiceResult.Invalid(ReportRules.ToErrors("definition", checkErrors.Distinct().ToList())))
            : (normalized, null);
    }
}
