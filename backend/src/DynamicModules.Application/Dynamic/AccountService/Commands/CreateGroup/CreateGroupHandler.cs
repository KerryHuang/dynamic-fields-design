using System.Security.Cryptography;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.CreateGroup;

public class CreateGroupHandler(IAccountRepository accounts, IUnitOfWork uow, RequiredTextGuard requiredText) : IRequestHandler<CreateGroupRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CreateGroupRequest request, CancellationToken ct)
    {
        var name = request.Name.Trim();
        if (await accounts.GroupNameExistsAsync(request.Locale, name, null, ct))
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["name"] = ["已有同名的群組"] });

        var text = new I18nText();
        text.Set(request.Locale, name);
        // 每個必填語系也寫同一個名稱（D5，使用者 2026-11-20 裁決），否則其他語系看到空白名稱
        foreach (var locale in requiredText.RequiredLocales) text.Set(locale, name);
        var group = new PermissionGroup
        {
            Key = "g-" + RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz0123456789", 8),
            NameI18n = text
        };
        accounts.AddGroup(group);
        await uow.CommitAsync(ct);
        return ServiceResult.Created(new AdminGroupResponse(group.Id, group.Key, group.NameI18n, []));
    }
}
