using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.RenameGroup;

public class RenameGroupHandler(IAccountRepository accounts, IUnitOfWork uow) : IRequestHandler<RenameGroupRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(RenameGroupRequest request, CancellationToken ct)
    {
        var group = await accounts.GetGroupTrackedAsync(request.GroupId, ct);
        if (group is null) return ServiceResult.NotFound("群組不存在");
        var name = request.Name.Trim();
        if (await accounts.GroupNameExistsAsync(request.Locale, name, group.Id, ct))
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["name"] = ["已有同名的群組"] });

        // 重新指派整個 I18nText，EF 才偵測得到 JSON 欄位變更；只改這個語系，其他語系保留
        var values = group.NameI18n.Values.ToDictionary(kv => kv.Key, kv => kv.Value);
        values[request.Locale] = name;
        group.NameI18n = new I18nText(values);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(new AdminGroupResponse(group.Id, group.Key, group.NameI18n, []));
    }
}
