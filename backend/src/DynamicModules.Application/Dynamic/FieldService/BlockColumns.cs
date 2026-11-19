using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Policies;

namespace DynamicModules.Application.Dynamic.FieldService;

/// <summary>區塊每列欄數的政策（A25）：新區塊＝min(模組的 form.defaultColumns, 全系統 form.maxColumns)。</summary>
public sealed class BlockColumns(IPolicyReader policies)
{
    /// <param name="moduleId">模組還沒建立（建立模組時的第一個區塊）傳 null，取全系統值。</param>
    public async Task<int> ForNewBlockAsync(long? moduleId, CancellationToken ct) =>
        Math.Min(await policies.IntAsync(PolicyCatalog.FormDefaultColumns, moduleId, ct), await MaxAsync(ct));

    public Task<int> MaxAsync(CancellationToken ct) => policies.IntAsync(PolicyCatalog.FormMaxColumns, null, ct);
}
