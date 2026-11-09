using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.FieldService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.Rollups;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Formulas;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Rules;
using MediatR;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.SetFieldStatus;

public class SetFieldStatusHandler(IModuleRepository modules, IRecordRepository records, IUnitOfWork uow, FieldTypeRegistry registry,
    ICurrentUser user, IBusinessTime time, RollupRecalc rollups, IEventFactRepository facts,
    Dynamic.LedgerService.ImmutableDataGuard immutable)
    : IRequestHandler<SetFieldStatusRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(SetFieldStatusRequest request, CancellationToken ct)
    {
        var module = await modules.GetByFieldIdAsync(request.FieldId, ct);
        if (module is null) return ServiceResult.NotFound("找不到欄位");

        if (request.Status == FieldStatus.Inactive && module.TitleFieldId == request.FieldId)
            return FieldRules.Invalid("Status", "記錄標題欄位不能停用");
        if (request.Status == FieldStatus.Inactive && module.TitleFieldIds().Contains(request.FieldId))
            return FieldRules.Invalid("Status", "標題樣板用到的欄位不能停用");

        if (request.Status == FieldStatus.Inactive && module.Workflow?.StatusFieldId == request.FieldId)
            return FieldRules.Invalid("Status", "這是狀態機的狀態欄位，請先移除狀態機");

        var field = module.Blocks.SelectMany(b => b.Fields).Single(f => f.Id == request.FieldId);
        if (request.Status == FieldStatus.Inactive && field is { Type: FieldType.Reference, Config.Relation: ReferenceRelation.Detail })
            return FieldRules.Invalid("Status", "明細的表頭欄位不能停用");

        if (request.Status == FieldStatus.Inactive
            && (module.Blocks.SelectMany(b => b.Fields).Any(f =>
                    f.Type == FieldType.Formula && f.Status == FieldStatus.Active && f.Id != request.FieldId
                    && FormulaGraph.RefsOf(f).Contains(request.FieldId))
                || module.Rules.Any(r => r.Active && RuleReferences.FieldIdsOf(r).Contains(request.FieldId))))
            return FieldRules.Invalid("Status", "此欄位被公式或規則引用，不能停用");

        if (request.Status == FieldStatus.Inactive
            && (await modules.ListRollupsBySourceModuleAsync(module.Id, ct))
                .Any(x => ModuleRelations.RollupRefsOf(x.Rollup).Contains(request.FieldId)))
            return FieldRules.Invalid("Status", "此欄位被彙總欄位引用，不能停用");

        if (request.Status == FieldStatus.Active && field.Status == FieldStatus.Inactive && field.Type == FieldType.Rollup)
        {
            // 停用期間來源、對象或條件欄位可能已被停用或改型別，重新啟用前重驗設定並寫回重推的 ResultType、Scale
            var (badRollup, derived) = await FieldRules.CheckRollupAsync(module, field.Config, modules, registry, ct);
            if (badRollup is not null) return badRollup;
            field.Config = derived;
        }

        var reactivated = field.Status == FieldStatus.Inactive && request.Status == FieldStatus.Active;
        // 重新啟用公式或彙總欄位會整批重算並寫回記錄值；Closed 的資料唯讀。停用本身不動資料，照常放行
        if (reactivated && field.Type == FieldType.Formula && await ClosedDataGuard.RejectFormulaRecalcAsync(module, modules, ct) is { } closedFormula)
            return closedFormula;
        if (reactivated && field.Type == FieldType.Rollup && ClosedDataGuard.Reject(module) is { } closedRollup) return closedRollup;
        if (reactivated && field.Type is FieldType.Formula or FieldType.Rollup && await immutable.RejectAsync(module, ct) is { } ledgerFrozen) return ledgerFrozen;

        var now = time.Now;
        field.Status = request.Status;
        // 停用期間存檔不寫此欄索引列，重新啟用時補回；公式欄位停用期間值也沒更新，改整批重算（同時重建索引）
        if (reactivated && field.Type == FieldType.Formula)
            await FormulaRecalc.RecalculateAsync(module, records, registry, rollups, facts, time.Today, ct);
        else if (reactivated && field.Type == FieldType.Rollup)
        {
            // 重算只在值有變時寫索引；停用期間缺的索引列另外補回
            await rollups.RecalculateModuleAsync(module, time.Today, ct);
            await RecordIndexBackfill.RebuildAsync(records, module, field, registry, ct);
        }
        else if (reactivated && RecordIndexer.ShouldIndex(field))
            await RecordIndexBackfill.RebuildAsync(records, module, field, registry, ct);
        field.StampModified(user.UserId, now);
        module.TouchVersion(user.UserId, now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(field.ToResponse());
    }
}
