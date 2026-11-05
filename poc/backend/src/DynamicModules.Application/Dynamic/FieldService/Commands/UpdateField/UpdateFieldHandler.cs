using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.FieldService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.Rollups;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.UpdateField;

public class UpdateFieldHandler(
    IModuleRepository modules, IRecordRepository records, IUnitOfWork uow, FieldTypeRegistry registry, ICurrentUser user, IBusinessTime time,
    RollupRecalc rollups)
    : IRequestHandler<UpdateFieldRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdateFieldRequest request, CancellationToken ct)
    {
        var module = await modules.GetByFieldIdAsync(request.FieldId, ct);
        if (module is null) return ServiceResult.NotFound("找不到欄位");
        var oldBlock = module.Blocks.Single(b => b.Fields.Any(f => f.Id == request.FieldId));
        var field = oldBlock.Fields.Single(f => f.Id == request.FieldId);

        if (request.Type != field.Type) return FieldRules.Invalid("Type", "欄位型別建立後不能更改");
        var newBlock = module.Blocks.FirstOrDefault(b => b.Id == request.BlockId);
        if (newBlock is null) return FieldRules.Invalid("BlockId", "區塊不屬於此模組");

        var invalid = FieldRules.CheckDefault(registry, field, request.Config, await modules.ListUserIdsAsync(ct), out var config);
        if (invalid is not null) return invalid;

        var badDynamic = FieldRules.CheckDynamicDefaults(
            module, field, config, await FieldRules.ParentOfAsync(module, modules, ct));
        if (badDynamic is not null) return badDynamic;

        if (field.Type == FieldType.Reference)
        {
            if (config.RefTarget != field.Config.RefTarget)
                return FieldRules.Invalid("Config.RefTarget", FieldRules.RefTargetLockedError);
            if ((config.Relation ?? ReferenceRelation.Lookup) != (field.Config.Relation ?? ReferenceRelation.Lookup))
                return FieldRules.Invalid("Config.Relation", FieldRules.RelationLockedError);
            if (config.Relation == ReferenceRelation.Detail)
                config = config with { Required = true, OnDelete = ReferenceOnDelete.Cascade };
            var bad = FieldRules.CheckReference(config, targetOk: true);
            if (bad is not null) return bad;
        }

        if (field.Type == FieldType.Formula)
        {
            var bad = FieldRules.CheckFormula(module, field.Id, config);
            if (bad is not null) return bad;
            var probe = new Field { Id = field.Id, Type = FieldType.Formula, Config = config };
            if (ModuleRelations.DependsOnRollup(module, probe)
                && (await modules.ListRollupsBySourceModuleAsync(module.Id, ct))
                    .Any(x => ModuleRelations.RollupDependenciesOf(module, x.Rollup).Contains(field.Id)))
                return FieldRules.Invalid("Config.Formula", "公式不正確：被彙總欄位引用的公式不能引用彙總欄位");
        }

        var badScope = FieldRules.CheckScope(module, field.Type, config);
        if (badScope is not null) return badScope;
        if (config.Scope != field.Config.Scope) return FieldRules.Invalid("Config.Scope", FieldRules.ScopeLockedError);

        if (field.Type == FieldType.Rollup)
        {
            var (badRollup, derived) = await FieldRules.CheckRollupAsync(module, config, modules, registry, ct);
            if (badRollup is not null) return badRollup;
            config = derived;
        }

        var (badDependent, rederived) = await RecheckDependentRollupsAsync(module, field, config, ct);
        if (badDependent is not null) return badDependent;

        var now = time.Now;
        if (newBlock.Id != oldBlock.Id)
        {
            field.Sequence = newBlock.Fields.Select(f => f.Sequence).DefaultIfEmpty(0).Max() + 1;
            oldBlock.Fields.Remove(field);
            newBlock.Fields.Add(field);
            field.BlockId = newBlock.Id;
        }

        field.LabelI18n = request.Label;
        var wasIndexed = RecordIndexer.ShouldIndex(field);
        var old = field.Config;
        field.Config = config;
        var isIndexed = RecordIndexer.ShouldIndex(field);
        if (wasIndexed != isIndexed) await RecordIndexBackfill.RebuildAsync(records, module, field, registry, ct);

        // 彙總對象的型別或小數位變了：寫回重推的 ResultType、Scale；公式重算會連帶重算這些父模組，否則逐一重算
        var rederivedParents = new List<Module>();
        foreach (var group in rederived.GroupBy(r => r.ParentId))
        {
            var parent = (await modules.GetByIdAsync(group.Key, includeChildren: true, ct))!;
            foreach (var (_, rollupId, derivedConfig) in group)
                parent.Blocks.SelectMany(b => b.Fields).Single(f => f.Id == rollupId).Config = derivedConfig;
            parent.TouchVersion(user.UserId, now);
            rederivedParents.Add(parent);
        }

        if (field.Type == FieldType.Formula
            && (old.Formula != config.Formula || old.Scale != config.Scale || old.ResultType != config.ResultType))
            await FormulaRecalc.RecalculateAsync(module, records, registry, rollups, time.Today, ct);
        else
        {
            if (field is { Type: FieldType.Rollup, Status: FieldStatus.Active } && RollupChanged(old.Rollup, config.Rollup))
                await rollups.RecalculateModuleAsync(module, time.Today, ct);
            foreach (var parent in rederivedParents)
                await rollups.RecalculateModuleAsync(parent, time.Today, ct);
        }
        field.StampModified(user.UserId, now);
        module.TouchVersion(user.UserId, now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(field.ToResponse());
    }

    /// <summary>
    /// 欄位被啟用中彙總引用（來源、彙總對象、條件）時，用新設定重驗那些彙總：條件失效或推導失敗回 400（彙總設定的訊息），
    /// 否則回傳 ResultType 或 Scale 有變的彙總與重推後的設定。CheckRollupAsync 取得的來源模組是同一個追蹤中的實體，所以暫時換上新設定。
    /// </summary>
    private async Task<(ServiceResult? Error, List<(long ParentId, long RollupId, FieldConfig Config)> Rederived)>
        RecheckDependentRollupsAsync(Module module, Field field, FieldConfig config, CancellationToken ct)
    {
        var rederived = new List<(long, long, FieldConfig)>();
        var dependents = (await modules.ListRollupsBySourceModuleAsync(module.Id, ct))
            .Where(x => ModuleRelations.RollupRefsOf(x.Rollup).Contains(field.Id)).ToList();
        if (dependents.Count == 0) return (null, rederived);

        var previous = field.Config;
        field.Config = config;
        try
        {
            foreach (var (parent, rollup, _) in dependents)
            {
                var (bad, derived) = await FieldRules.CheckRollupAsync(parent, rollup.Config, modules, registry, ct);
                if (bad is not null) return (bad, rederived);
                if (derived.ResultType != rollup.Config.ResultType || derived.Scale != rollup.Config.Scale)
                    rederived.Add((parent.Id, rollup.Id, derived));
            }
        }
        finally
        {
            field.Config = previous;
        }
        return (null, rederived);
    }

    /// <summary>JsonElement 的相等比的是文件參照，條件改比原始文字。</summary>
    private static bool RollupChanged(RollupConfig? a, RollupConfig? b) =>
        a is null || b is null
            ? a != b
            : a with { Condition = null } != b with { Condition = null } || a.Condition?.GetRawText() != b.Condition?.GetRawText();
}
