using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ImportService.Data;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.Rollups;
using MediatR;

namespace DynamicModules.Application.Dynamic.ImportService.Commands.ImportRecords;

/// <summary>確認匯入（決策 6）：不信任前一次預覽，重新驗證；任何一列有錯整批不寫。</summary>
public class ImportRecordsHandler(ImportPlanner planner, RecordCreator creator, RollupRecalc rollups)
    : IRequestHandler<ImportRecordsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ImportRecordsRequest request, CancellationToken ct)
    {
        var (plan, failure) = await planner.PlanAsync(request.ModuleKey, request.Content, ct);
        if (plan is null) return failure!;
        var errors = plan.Rows
            .SelectMany(r => r.Errors.Select(e => (Key: $"rows[{r.Line}].{e.Key}", e.Value)))
            .ToDictionary(e => e.Key, e => e.Value);
        if (errors.Count > 0) return ServiceResult.Invalid(errors);

        foreach (var row in plan.Rows) await creator.InsertAsync(plan.Module, row.Values, null, null, ct);
        // 同 POST records：子記錄寫入後重算被參照的父記錄彙總（新記錄彼此不會互相參照，不需要跳過任何記錄）
        await rollups.AfterWriteAsync(plan.Module,
            plan.Rows.Select(r => ((IReadOnlyDictionary<string, JsonElement?>?)null, (IReadOnlyDictionary<string, JsonElement?>?)r.Values)),
            new HashSet<long>(), ct);
        return ServiceResult.Success(new ImportResultResponse(plan.Rows.Count));
    }
}
