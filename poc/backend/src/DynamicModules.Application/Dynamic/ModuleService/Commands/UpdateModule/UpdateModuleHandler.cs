using System.Globalization;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.ModuleService.Commands.UpdateModule;

public class UpdateModuleHandler(
    IModuleRepository modules, IRecordRepository records, IPermissionRepository permissions, IUnitOfWork uow, ICurrentUser user, IBusinessTime time,
    Dynamic.LedgerService.ImmutableDataGuard immutable, TitleRewriteGuard fixedRecords)
    : IRequestHandler<UpdateModuleRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdateModuleRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.Key, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound("找不到模組");

        // 換標題欄位或標題樣板會重寫全部既有記錄的 Title；Closed 的資料唯讀、已固定的記錄不能改，其餘欄位（名稱、圖示、區段）照常可改
        var titleChanged = module.TitleFieldId != request.TitleFieldId || module.TitleTemplate != request.TitleTemplate;
        if (titleChanged && ClosedDataGuard.Reject(module) is { } closed) return closed;
        if (titleChanged && await immutable.RejectAsync(module, ct) is { } ledgerFrozen) return ledgerFrozen;
        List<Record> rows = titleChanged ? await records.ListAllByModuleAsync(module.Id, ct) : [];
        if (titleChanged && await fixedRecords.RejectAsync(module, rows, ct) is { } fixedRecord) return fixedRecord;

        if (module.Kind == ModuleKind.Extension)
        {
            if (request.TitleFieldId is not null) return TitleInvalid("擴充模組的標題取自既有資料");
            if (request.TitleTemplate is not null) return TemplateInvalid("擴充模組的標題取自既有資料");
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
        else if (module.Status is ModuleStatus.Active or ModuleStatus.Closed)
            return TitleInvalid("啟用中的模組必須有記錄標題欄位");

        if (module.Kind != ModuleKind.Extension && request.TitleTemplate is { } template
            && module.TitleTemplate != template && await TemplateErrorAsync(module, template, ct) is { } templateError)
            return TemplateInvalid(templateError);

        module.NameI18n = request.Name;
        module.Icon = request.Icon;
        module.SetSection(request.Section);
        module.TitleFieldId = request.TitleFieldId;
        module.TitleTemplate = request.TitleTemplate;
        if (titleChanged)
            foreach (var record in rows)
                record.Title = RecordValueProcessor.TitleOf(module, RecordValueProcessor.Parse(record.ValuesJson));
        module.TouchVersion(user.UserId, time.Now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(module.ToResponse());
    }

    /// <summary>樣板語法、欄位存在、啟用中、型別，以及不能是有隱藏例外的欄位（隱藏會讓看不到的人連標題都看不到）。</summary>
    private async Task<string?> TemplateErrorAsync(Module module, string template, CancellationToken ct)
    {
        var (parts, error) = TitleTemplate.Parse(template);
        if (error is not null) return error;
        var fields = module.Blocks.SelectMany(b => b.Fields).ToDictionary(f => f.Id.ToString(CultureInfo.InvariantCulture));
        var used = new List<Field>();
        foreach (var token in parts.Where(p => p.Token is not null).Select(p => p.Token!).Distinct())
        {
            if (!fields.TryGetValue(token, out var f)) return $"標題樣板引用了不存在的欄位 {token}";
            var label = f.LabelI18n.Resolve(I18nText.FallbackLocale);
            if (f.Status != FieldStatus.Active) return $"標題樣板不能引用已停用的欄位 {label}";
            if (!TitleTemplate.AllowedTypes.Contains(f.Type)) return $"標題樣板不能引用 {label}（{TitleTemplate.AllowedTypesText}）";
            used.Add(f);
        }
        var hidden = (await permissions.LoadModuleRowsAsync(module, ct)).FieldPermissions
            .Where(p => p.Access == FieldAccess.Hidden).Select(p => p.FieldId).ToHashSet();
        return used.FirstOrDefault(f => hidden.Contains(f.Id)) is { } h
            ? $"標題樣板不能引用有隱藏例外的欄位 {h.LabelI18n.Resolve(I18nText.FallbackLocale)}"
            : null;
    }

    private static ServiceResult TitleInvalid(string message) =>
        ServiceResult.Invalid(new Dictionary<string, string[]> { ["TitleFieldId"] = [message] });

    private static ServiceResult TemplateInvalid(string message) =>
        ServiceResult.Invalid(new Dictionary<string, string[]> { ["TitleTemplate"] = [message] });
}
