using System.Globalization;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Uploads;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.DemoService.Commands.GenerateDemoData;

/// <summary>衝突檢查（交易已由 TransactionBehavior 開始）→ 建示範人員 → 建結構 → 規劃各情境的步驟 → 依模擬時間排序執行。</summary>
public class GenerateDemoDataHandler(DemoPreflight preflight, IDemoPeople people, DemoStructure structure,
    ISystemSettingRepository settings, IUnitOfWork uow)
    : IRequestHandler<GenerateDemoDataRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GenerateDemoDataRequest request, CancellationToken ct)
    {
        var conflicts = await preflight.ConflictsAsync(ct);
        if (conflicts.Count > 0)
            return ServiceResult.ConflictWith("已有相同的資料，未產生 Demo 資料", new DemoConflicts([.. conflicts]));

        await people.AddAsync(ct);

        var (context, failed) = await structure.BuildAsync(request.Today, ct);
        if (failed is not null) return failed;

        var steps = DemoTimeline.Plan(request.Today, DemoCatalog.Scenarios);
        // 時間軸裡有上傳設計圖與留言附件：Demo 站的環境停用上傳，伺服器自己寫入時略過那個開關（其餘檢查照常）
        using (ServerUploadScope.Begin())
            failed = await DemoTimeline.RunAsync(context!, steps, request.Progress, ct);
        if (failed is not null) return failed;
        // 資料日期停在這天；Demo 登入開啟時報表以它為基準日
        await settings.SetAsync(SystemSetting.DemoGeneratedOn, request.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ct);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(new DemoGenerateResult(steps.Count));
    }
}
