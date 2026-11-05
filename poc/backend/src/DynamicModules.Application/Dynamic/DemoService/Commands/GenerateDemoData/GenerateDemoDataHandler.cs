using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.DemoService.Commands.GenerateDemoData;

/// <summary>衝突檢查（交易已由 TransactionBehavior 開始）→ 建結構。記錄步驟由後續接上。</summary>
public class GenerateDemoDataHandler(DemoPreflight preflight, DemoStructure structure)
    : IRequestHandler<GenerateDemoDataRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GenerateDemoDataRequest request, CancellationToken ct)
    {
        var conflicts = await preflight.ConflictsAsync(ct);
        if (conflicts.Count > 0)
            return ServiceResult.ConflictWith("已有同代碼的模組，未產生 Demo 資料", new DemoConflicts([.. conflicts]));

        var (_, failed) = await structure.BuildAsync(request.Today, ct);
        if (failed is not null) return failed;
        return ServiceResult.Success(new DemoGenerateResult(0));
    }
}
