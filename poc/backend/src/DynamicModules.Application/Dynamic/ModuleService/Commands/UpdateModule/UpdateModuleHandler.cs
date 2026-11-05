using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ModuleService.Commands.UpdateModule;

public class UpdateModuleHandler(
    IModuleRepository modules, IRecordRepository records, IPermissionRepository permissions, IUnitOfWork uow, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<UpdateModuleRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdateModuleRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.Key, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound("找不到模組");

        if (module.Kind == ModuleKind.Extension)
        {
            if (request.TitleFieldId is not null) return TitleInvalid("擴充模組的標題取自既有資料");
        }
        else if (request.TitleFieldId is { } titleId)
        {
            var title = module.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == titleId);
            if (title is null) return TitleInvalid("記錄標題欄位不存在");
            if (title is not { Status: FieldStatus.Active, Type: FieldType.Text or FieldType.AutoNumber })
                return TitleInvalid("記錄標題欄位必須是啟用中的文字或自動編號欄位");
            if (module.TitleFieldId != titleId
                && (await permissions.LoadModuleRowsAsync(module, ct)).FieldPermissions
                    .Any(p => p.FieldId == titleId && p.Access == FieldAccess.Hidden))
                return TitleInvalid("記錄標題欄位不能是有隱藏例外的欄位");
        }
        else if (module.Status == ModuleStatus.Active)
            return TitleInvalid("啟用中的模組必須有記錄標題欄位");

        module.NameI18n = request.Name;
        module.Icon = request.Icon;
        module.Section = request.Section;
        var titleChanged = module.TitleFieldId != request.TitleFieldId;
        module.TitleFieldId = request.TitleFieldId;
        if (titleChanged)
            foreach (var record in await records.ListAllByModuleAsync(module.Id, ct))
                record.Title = RecordValueProcessor.TitleOf(module, RecordValueProcessor.Parse(record.ValuesJson));
        module.TouchVersion(user.UserId, time.Now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(module.ToResponse());
    }

    private static ServiceResult TitleInvalid(string message) =>
        ServiceResult.Invalid(new Dictionary<string, string[]> { ["TitleFieldId"] = [message] });
}
