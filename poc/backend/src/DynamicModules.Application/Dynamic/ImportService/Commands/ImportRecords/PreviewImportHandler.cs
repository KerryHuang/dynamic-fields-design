using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ImportService.Data;
using MediatR;

namespace DynamicModules.Application.Dynamic.ImportService.Commands.ImportRecords;

public class PreviewImportHandler(ImportPlanner planner) : IRequestHandler<PreviewImportRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(PreviewImportRequest request, CancellationToken ct)
    {
        var (plan, failure) = await planner.PlanAsync(request.ModuleKey, request.Content, ct);
        if (plan is null) return failure!;
        var errors = plan.Rows.Where(r => r.Errors.Count > 0).Select(r => new ImportRowError(r.Line, r.Errors)).ToList();
        return ServiceResult.Success(new ImportPreviewResponse(plan.Columns, plan.Rows.Count, plan.Rows.Count - errors.Count, errors));
    }
}
