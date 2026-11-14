using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Application.Dynamic.RecordService.Data;
using MediatR;

namespace DynamicModules.Application.Dynamic.PrintService;

internal static class PrintModuleLookup
{
    public static async Task<Module?> DesignableAsync(IModuleRepository modules, string key, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(key, includeChildren: true, ct);
        return module is null || ModuleRelations.IsDetail(module) ? null : module;
    }
}

public class GetPrintTemplateDefinitionsHandler(IModuleRepository modules, IPrintTemplateRepository templates)
    : IRequestHandler<GetPrintTemplateDefinitionsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetPrintTemplateDefinitionsRequest request, CancellationToken ct)
    {
        var module = await PrintModuleLookup.DesignableAsync(modules, request.ModuleKey, ct);
        if (module is null) return ServiceResult.NotFound(RecordMapper.ModuleNotFound);
        return ServiceResult.Success((await templates.ListByModuleAsync(module.Id, ct)).Select(t => t.ToResponse()).ToList());
    }
}

public class CreatePrintTemplateHandler(
    IModuleRepository modules, IPrintTemplateRepository templates, PrintTemplateValidator validator, IUnitOfWork uow,
    ICurrentUser user, IBusinessTime time) : IRequestHandler<CreatePrintTemplateRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CreatePrintTemplateRequest request, CancellationToken ct)
    {
        var module = await PrintModuleLookup.DesignableAsync(modules, request.ModuleKey, ct);
        if (module is null) return ServiceResult.NotFound(RecordMapper.ModuleNotFound);
        var (invalid, body) = await validator.ValidateAsync(module, request.Input, isNew: true, ct);
        if (invalid is not null) return invalid;
        if (await templates.GetAsync(module.Id, request.Input.Key, ct) is not null) return ServiceResult.Conflict("列印範本代碼已存在");

        var template = new PrintTemplate
        {
            ModuleId = module.Id, Key = request.Input.Key, NameI18n = request.Input.Name, Body = body, Active = request.Input.Active
        };
        template.StampCreated(user.UserId, time.Now);
        templates.Add(template);
        await uow.CommitAsync(ct);
        return ServiceResult.Created(template.ToResponse());
    }
}

public class UpdatePrintTemplateHandler(
    IModuleRepository modules, IPrintTemplateRepository templates, PrintTemplateValidator validator, IUnitOfWork uow,
    ICurrentUser user, IBusinessTime time) : IRequestHandler<UpdatePrintTemplateRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdatePrintTemplateRequest request, CancellationToken ct)
    {
        var module = await PrintModuleLookup.DesignableAsync(modules, request.ModuleKey, ct);
        if (module is null) return ServiceResult.NotFound(RecordMapper.ModuleNotFound);
        var template = await templates.GetAsync(module.Id, request.Key, ct);
        if (template is null) return ServiceResult.NotFound(PrintTemplateRules.NotFound);
        if (request.Input.Key != template.Key)
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["key"] = ["列印範本代碼不能更改"] });
        var (invalid, body) = await validator.ValidateAsync(module, request.Input, isNew: false, ct);
        if (invalid is not null) return invalid;

        template.NameI18n = request.Input.Name;
        template.Body = body;
        template.Active = request.Input.Active;
        template.StampModified(user.UserId, time.Now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(template.ToResponse());
    }
}

public class DeletePrintTemplateHandler(IModuleRepository modules, IPrintTemplateRepository templates, IUnitOfWork uow)
    : IRequestHandler<DeletePrintTemplateRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DeletePrintTemplateRequest request, CancellationToken ct)
    {
        var module = await PrintModuleLookup.DesignableAsync(modules, request.ModuleKey, ct);
        if (module is null) return ServiceResult.NotFound(RecordMapper.ModuleNotFound);
        var template = await templates.GetAsync(module.Id, request.Key, ct);
        if (template is null) return ServiceResult.NotFound(PrintTemplateRules.NotFound);
        templates.Remove(template);
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}

public class GetPrintTemplatesHandler(
    IModuleRepository modules, IPrintTemplateRepository templates, IDynamicAccessGuard guard, IRecordRepository records,
    ApprovalService.ApprovalReadGrant grant) : IRequestHandler<GetPrintTemplatesRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetPrintTemplatesRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (!module.IsReadable() || ModuleRelations.IsDetail(module)) return ServiceResult.NotFound(RecordMapper.ModuleNotFound);
        var readAccess = await guard.AuthorizeAsync(module, PermissionAction.Read, ct);
        var access = readAccess.Permissions is null ? readAccess : await guard.AuthorizeAsync(module, PermissionAction.PrintExport, ct);
        if (access.Permissions is null)
        {
            // 簽核人的臨時讀取（簽核設計 §10.1）：只在一般權限不成立、而且指定了一筆記錄時；不存在、別的模組、不是可動作者都回原本的拒絕
            if (request.RecordId is not { } recordId) return access.Denied!;
            var record = await records.GetAsync(recordId, ct);
            if (record is null || record.ModuleId != module!.Id) return access.Denied!;
            if (await grant.ForRecordAsync(module, record, ct) is null) return access.Denied!;
        }
        var active = (await templates.ListByModuleAsync(module!.Id, ct)).Where(t => t.Active).Select(t => new PrintTemplateOption(t.Key, t.NameI18n));
        return ServiceResult.Success(active.ToList());
    }
}
