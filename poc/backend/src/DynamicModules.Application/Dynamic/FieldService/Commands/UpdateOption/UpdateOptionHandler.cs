using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.UpdateOption;

public class UpdateOptionHandler(
    IModuleRepository modules, IRecordRepository records, IUnitOfWork uow, ICurrentUser user, IBusinessTime time,
    Dynamic.LedgerService.ImmutableDataGuard immutable, Dynamic.ModuleService.TitleRewriteGuard fixedRecords)
    : IRequestHandler<UpdateOptionRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdateOptionRequest request, CancellationToken ct)
    {
        var module = await modules.GetByOptionIdAsync(request.OptionId, ct);
        if (module is null) return ServiceResult.NotFound("找不到選項");

        var now = time.Now;
        var field = module.Blocks.SelectMany(b => b.Fields).Single(f => f.Options.Any(o => o.Id == request.OptionId));
        var option = field.Options.Single(o => o.Id == request.OptionId);
        // 標題樣板用到這個下拉欄位時，改名會重寫既有記錄的 Title（跟換標題欄位一樣受 Closed、帳本凍結與已固定的記錄限制）
        var retitle = module.TitleTemplate is not null && module.TitleFieldIds().Contains(field.Id)
            && option.LabelI18n.Resolve(I18nText.FallbackLocale) != request.Label.Resolve(I18nText.FallbackLocale);
        if (retitle && ClosedDataGuard.Reject(module) is { } closed) return closed;
        if (retitle && await immutable.RejectAsync(module, ct) is { } frozen) return frozen;
        List<Domain.Entities.Dynamic.Record> rows = retitle ? await records.ListAllByModuleAsync(module.Id, ct) : [];
        if (retitle && await fixedRecords.RejectAsync(module, rows, ct) is { } fixedRecord) return fixedRecord;
        option.LabelI18n = request.Label;
        option.Color = request.Color;
        option.Active = request.Active;
        option.StampModified(user.UserId, now);
        if (retitle)
            foreach (var record in rows)
                record.Title = RecordValueProcessor.TitleOf(module, RecordValueProcessor.Parse(record.ValuesJson));
        module.TouchVersion(user.UserId, now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(option.ToResponse());
    }
}
