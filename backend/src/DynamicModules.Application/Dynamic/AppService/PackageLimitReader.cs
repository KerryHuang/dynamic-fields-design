using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Policies;

namespace DynamicModules.Application.Dynamic.AppService;

/// <summary>
/// 套件的政策上限檢查（全系統生效值）：任務規則（A1、A2）、簽核（A3、A37）、設計上限（A16）。匯入、開啟、主檔合併共用；
/// 靜態的 <see cref="AppPackageValidator.Validate"/> 不讀資料庫，所以這些數量檢查不在那裡，內建套件啟動時也不檢查。
/// </summary>
public sealed class PackageLimitReader(IPolicyReader policies)
{
    public async Task<IReadOnlyList<string>> ErrorsAsync(AppPackageData p, CancellationToken ct)
    {
        var errors = new List<string>(AppPackageValidator.TaskRuleLimitErrors(p,
            await policies.IntAsync(PolicyCatalog.TaskMaxRulesPerPackage, null, ct), await policies.IntAsync(PolicyCatalog.TaskMaxTasksPerRule, null, ct)));
        if (p.ApprovalFlows.Count > 0)
            errors.AddRange(AppPackageValidator.ApprovalLimitErrors(p, await AppOpenPlanner.SystemApprovalLimitsAsync(policies, ct)));
        errors.AddRange(AppPackageValidator.DesignLimitErrors(p, await DesignLimitsAsync(ct)));
        return errors;
    }

    private async Task<DesignLimits> DesignLimitsAsync(CancellationToken ct) => new(
        await policies.IntAsync(PolicyCatalog.DesignMaxGroupsPerPackage, null, ct),
        await policies.IntAsync(PolicyCatalog.DesignMaxModulesPerPackage, null, ct),
        await policies.IntAsync(PolicyCatalog.DesignMaxFieldsPerModule, null, ct),
        await policies.IntAsync(PolicyCatalog.DesignMaxOptionsPerField, null, ct),
        await policies.IntAsync(PolicyCatalog.DesignMaxRulesPerModule, null, ct),
        await policies.IntAsync(PolicyCatalog.DesignMaxViewsPerModule, null, ct),
        await policies.IntAsync(PolicyCatalog.DesignMaxIntegrationsPerPackage, null, ct));
}
