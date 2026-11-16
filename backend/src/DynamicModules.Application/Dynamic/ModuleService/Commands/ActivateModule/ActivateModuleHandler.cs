using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;

public class ActivateModuleHandler(
    IModuleRepository modules, IProgramRepository programs, IUnitOfWork uow, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<ActivateModuleRequest, ServiceResult>
{
    private const string ClosedMessage = "請由應用程式重新開啟或關閉";

    public async Task<ServiceResult> Handle(ActivateModuleRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.Key, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound(MessageRef.Of(MessageText.ModuleNotFound));
        if (module.Status == ModuleStatus.Closed) return ServiceResult.Conflict(ClosedMessage);

        var title = module.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == module.TitleFieldId);
        if (module.Kind != ModuleKind.Extension && title is not { Status: FieldStatus.Active, Type: FieldType.Text or FieldType.AutoNumber })
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["TitleFieldId"] = ["啟用前必須指定記錄標題欄位"] });

        module.Status = ModuleStatus.Active;
        var program = await programs.GetAsync(module.ProgramCode, ct);
        if (program is not null) program.Active = true;
        module.TouchVersion(user.UserId, time.Now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}
