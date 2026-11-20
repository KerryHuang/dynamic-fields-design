using System.Security.Cryptography;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.CreateGroup;

public class CreateGroupHandler(IAccountRepository accounts, IUnitOfWork uow, RequiredTextGuard requiredText, RequestLocale requestLocale) : IRequestHandler<CreateGroupRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CreateGroupRequest request, CancellationToken ct)
    {
        var name = request.Name.Trim();
        // 沒指定語系用請求語系、指定了停用或不認得的用預設語系（D4）；同名檢查用決定後的語系
        var locale = requestLocale.For(request.Locale).Locale;
        if (await accounts.GroupNameExistsAsync(locale, name, null, ct))
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["name"] = ["已有同名的群組"] });

        var text = new I18nText();
        text.Set(locale, name);
        // 每個必填語系也寫同一個名稱（D5，使用者 2026-11-20 裁決），否則其他語系看到空白名稱
        foreach (var required in requiredText.RequiredLocales) text.Set(required, name);
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
