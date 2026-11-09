using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.FieldService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Application.Dynamic.Rollups;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Modules;
using MediatR;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.AddField;

public class AddFieldHandler(
    IModuleRepository modules, IRecordRepository records, IUnitOfWork uow, FieldTypeRegistry registry, ICurrentUser user, IBusinessTime time,
    ReferenceProviderRegistry providers, RollupRecalc rollups, IEventFactRepository facts,
    Dynamic.LedgerService.ImmutableDataGuard immutable, Common.Security.FieldLocks locks,
    Dynamic.AppService.SharedMasters.MasterDesigner masterDesigner)
    : IRequestHandler<AddFieldRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(AddFieldRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound("找不到模組");
        var block = module.Blocks.FirstOrDefault(b => b.Id == request.BlockId);
        if (block is null) return ServiceResult.NotFound("找不到區塊");
        if (request.Designer && await masterDesigner.RejectAddFieldAsync(module, block.Id, request.Type, request.Config, ct) is { } rejected) return rejected;

        // 新增公式或彙總欄位會替既有記錄算值並寫回；Closed 的資料唯讀
        if (request.Type == FieldType.Formula && !request.MasterBase
            && await ClosedDataGuard.RejectFormulaRecalcAsync(module, modules, ct) is { } closedFormula)
            return closedFormula;
        if (request.Type == FieldType.Rollup && ClosedDataGuard.Reject(module) is { } closedRollup) return closedRollup;
        if (request.Type is FieldType.Formula or FieldType.Rollup && await immutable.RejectAsync(module, ct) is { } ledgerFrozen) return ledgerFrozen;

        // 新增當下還沒有選項，下拉欄位的預設值要等加完選項後用 UpdateField 設定。
        var field = new Field { ModuleId = module.Id, BlockId = block.Id, Type = request.Type };
        var invalid = FieldRules.CheckDefault(registry, field, request.Config, await modules.ListUserIdsAsync(ct), out var config);
        if (invalid is not null) return invalid;

        var badDynamic = FieldRules.CheckDynamicDefaults(
            module, field, config, await FieldRules.ParentOfAsync(module, modules, ct));
        if (badDynamic is not null) return badDynamic;

        var badFill = await FieldRules.CheckFillAsync(module, field, 0, config, modules, ct);
        if (badFill is not null) return badFill;

        var badGuard = await FieldRules.CheckRefGuardAsync(field, config, modules, ct);
        if (badGuard is not null) return badGuard;

        if (request.Type == FieldType.Reference)
        {
            if (config.Relation == ReferenceRelation.Detail)
            {
                var notDetail = await FieldRules.CheckDetailAsync(module, config, modules, records, ct);
                if (notDetail is not null) return notDetail;
                config = config with { Required = true, OnDelete = ReferenceOnDelete.Cascade };
            }
            var targetOk = await FieldRules.IsValidRefTargetAsync(config.RefTarget, modules, providers, ct);
            var bad = FieldRules.CheckReference(config, targetOk);
            if (bad is not null) return bad;
        }

        if (request.Type == FieldType.Formula)
        {
            var bad = FieldRules.CheckFormula(module, 0, config);
            if (bad is not null) return bad;
        }

        if (request.Type == FieldType.Attachment && ModuleRelations.IsDetail(module))
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["Type"] = [FieldRules.AttachmentInDetailError] });

        var badScope = FieldRules.CheckScope(module, request.Type, config);
        if (badScope is not null) return badScope;

        if (request.Type == FieldType.Rollup)
        {
            var (badRollup, derived) = await FieldRules.CheckRollupAsync(module, config, modules, registry, ct);
            if (badRollup is not null) return badRollup;
            config = derived;
        }

        var now = time.Now;
        field.LabelI18n = request.Label;
        field.Config = config;
        field.Sequence = module.Blocks.SelectMany(b => b.Fields).Where(f => f.BlockId == block.Id)
            .Select(f => f.Sequence).DefaultIfEmpty(0).Max() + 1;
        field.Status = FieldStatus.Active;
        field.StampCreated(user.UserId, now);
        block.Fields.Add(field);
        await uow.CommitAsync(ct);

        // Field.Key = f_<Id> 需先有 Id；(ModuleId, Key) 唯一，所以先以空 Key 插入再回填。
        field.Key = $"f_{field.Id}";
        if (request.Type == FieldType.Formula)
            await FormulaRecalc.RecalculateAsync(module, records, registry, rollups, facts, time.Today, ct, locks);
        else if (request.Type == FieldType.Rollup)
            await rollups.RecalculateModuleAsync(module, time.Today, ct);
        module.TouchVersion(user.UserId, now);
        await uow.CommitAsync(ct);
        if (request.Designer) await masterDesigner.BindFieldAsync(module, field, ct);
        return ServiceResult.Created(field.ToResponse());
    }
}
