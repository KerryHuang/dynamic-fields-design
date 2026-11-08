using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Modules;
using MediatR;

namespace DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;

public class SaveModulePermissionsHandler(
    IModuleRepository modules, IPermissionRepository permissions, IUnitOfWork uow, EventService.EventHiddenGuard hiddenGuard)
    : IRequestHandler<SaveModulePermissionsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(SaveModulePermissionsRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.Key, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound("找不到模組");

        var body = request.Body;
        var allGroups = await permissions.ListGroupsAsync(ct);
        var groupIds = allGroups.Select(g => g.Id).ToHashSet();
        var userIds = await modules.ListUserIdsAsync(ct);
        var fields = module.Blocks.SelectMany(b => b.Fields).ToList();
        var fieldIds = fields.Select(f => f.Id).ToHashSet();
        var optionIds = fields.SelectMany(f => f.Options).Select(o => o.Id).ToHashSet();

        var errors = new Dictionary<string, string[]>();
        void Err(string key, string message) => errors[key] = [message];

        if (ModuleRelations.IsDetail(module) && (body.Groups.Count > 0 || body.Users.Count > 0))
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["groups"] = ["明細模組的權限沿用表頭模組"] });

        var seenGroups = new HashSet<long>();
        for (var i = 0; i < body.Groups.Count; i++)
        {
            var g = body.Groups[i];
            if (!groupIds.Contains(g.GroupId)) Err($"groups[{i}].groupId", "群組不存在");
            else if (!seenGroups.Add(g.GroupId)) Err($"groups[{i}].groupId", "設定重複");
        }

        var seenUsers = new HashSet<string>();
        for (var i = 0; i < body.Users.Count; i++)
        {
            var u = body.Users[i];
            if (!userIds.Contains(u.UserId)) Err($"users[{i}].userId", "使用者不存在");
            else if (!seenUsers.Add(u.UserId)) Err($"users[{i}].userId", "設定重複");
        }

        var seenFields = new HashSet<(long, long)>();
        for (var i = 0; i < body.Fields.Count; i++)
        {
            var f = body.Fields[i];
            if (!fieldIds.Contains(f.FieldId)) Err($"fields[{i}].fieldId", "欄位不屬於此模組");
            else if (!seenFields.Add((f.FieldId, f.GroupId))) Err($"fields[{i}].fieldId", "設定重複");
            if (!groupIds.Contains(f.GroupId)) Err($"fields[{i}].groupId", "群組不存在");
            if (f.Access == FieldAccess.Hidden && f.FieldId == module.TitleFieldId)
                Err($"fields[{i}].access", "標題欄位不能設為隱藏");
        }

        var seenOptions = new HashSet<long>();
        for (var i = 0; i < body.Options.Count; i++)
        {
            var o = body.Options[i];
            if (!optionIds.Contains(o.OptionId)) Err($"options[{i}].optionId", "選項不屬於此模組");
            else if (!seenOptions.Add(o.OptionId)) Err($"options[{i}].optionId", "設定重複");
            var seenOptionGroups = new HashSet<long>();
            for (var j = 0; j < o.GroupIds.Count; j++)
            {
                if (!groupIds.Contains(o.GroupIds[j])) Err($"options[{i}].groupIds[{j}]", "群組不存在");
                else if (!seenOptionGroups.Add(o.GroupIds[j])) Err($"options[{i}].groupIds[{j}]", "設定重複");
            }
        }

        if (errors.Count > 0) return ServiceResult.Invalid(errors);

        // 事後隱藏：被連動事件複製的來源欄位，沒有明確允許就不能設為隱藏
        var hiding = body.Fields.Where(f => f.Access == FieldAccess.Hidden).Select(f => f.FieldId).ToHashSet();
        if (await hiddenGuard.FindViolationAsync(module, hiding, ct) is { } violation)
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["fields"] = [violation] });

        var rows = new ModulePermissionRows(
            body.Groups.Where(g => !g.Privileges.IsEmpty).Select(g => Fill(new GroupProgramPrivilege { GroupId = g.GroupId }, module, g.Privileges)).ToList(),
            body.Users.Where(u => !u.Privileges.IsEmpty).Select(u => Fill(new UserProgramPrivilege { UserId = u.UserId }, module, u.Privileges)).ToList(),
            body.Groups.Where(g => g.Scope is not null).Select(g => new ModuleDataScope { ModuleId = module.Id, GroupId = g.GroupId, Scope = g.Scope!.Value }).ToList(),
            body.Fields.Select(f => new FieldPermission { FieldId = f.FieldId, GroupId = f.GroupId, Access = f.Access }).ToList(),
            body.Options.SelectMany(o => o.GroupIds.Select(g => new FieldOptionScope { OptionId = o.OptionId, GroupId = g })).ToList());

        await permissions.ReplaceModuleRowsAsync(module, rows, ct);
        await uow.CommitAsync(ct);
        return ServiceResult.Success((await permissions.LoadModuleRowsAsync(module, ct)).ToResponse(allGroups));
    }

    private static T Fill<T>(T row, Module module, PrivilegesDto p) where T : ProgramPrivilege
    {
        row.ProgramCode = module.ProgramCode;
        row.CanRead = p.Read; row.CanAdd = p.Add; row.CanEdit = p.Edit; row.CanDelete = p.Delete;
        row.CanPrint = p.PrintExport; row.CanAmt = p.Pricing; row.CanApprove = p.Approve;
        return row;
    }
}
