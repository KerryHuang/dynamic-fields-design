using System.Security.Cryptography;
using System.Text;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Application.Dynamic.SchemaService.Data;
using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Rules;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.SchemaService.Queries.GetModuleSchema;

/// <summary>
/// 組執行期 schema，執行期（GetModuleSchemaHandler）與設計器預覽（GetPreviewSchemaHandler）共用；
/// 權限由呼叫端傳入，這裡不做授權。
/// </summary>
public class ModuleSchemaBuilder(
    IModuleRepository modules, FieldTypeRegistry registry, ReferenceProviderRegistry providers, ILedgerRepository ledgers)
{
    /// <param name="detailPermissions">明細模組對目前呼叫端的權限；null 表示略過該明細。</param>
    /// <param name="permittedTransitions">使用者能做的狀態轉移；null 表示全部（設計器預覽）。</param>
    public async Task<ModuleSchemaResponse> BuildAsync(
        Module module, EffectivePermissions p, Func<Module, Task<EffectivePermissions?>> detailPermissions, CancellationToken ct,
        IReadOnlyCollection<WorkflowTransition>? permittedTransitions = null)
    {
        var (titleKey, blocks) = await BuildAsync(module, p, excludeFieldId: null, ct);
        var rules = await ToSchemaRulesAsync(module, blocks, ct);

        var (details, etagVersion, permHash) = await DetailsAsync(module, p, detailPermissions, ct);
        var workflow = WorkflowOf(module, blocks, permittedTransitions);
        if (workflow is not null)
            permHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                permHash + "|" + string.Join(",", workflow.Transitions.Select(t => $"{t.From}>{t.To}"))))) [..8].ToLowerInvariant();

        var closed = module.Status == ModuleStatus.Closed;
        var ledger = await ledgers.IsLedgerModuleAsync(module.Id, ct);
        return new ModuleSchemaResponse(
            module.Key, module.ProgramCode, module.NameI18n, module.Icon, titleKey, module.Version, blocks,
            new SchemaPermissions(p.Can(PermissionAction.Read), !closed && p.Can(PermissionAction.Add), !closed && !ledger && p.Can(PermissionAction.Edit),
                !closed && !ledger && p.Can(PermissionAction.Delete), p.Can(PermissionAction.PrintExport), p.Can(PermissionAction.Pricing)), rules, module.Kind, module.HostEntityKey,
            details, workflow, closed, ledger)
        { PermHash = permHash, ETagVersion = etagVersion };
    }

    /// <summary>狀態欄位對我隱藏時不外露狀態機。</summary>
    private static SchemaWorkflow? WorkflowOf(
        Module module, List<SchemaBlock> visibleBlocks, IReadOnlyCollection<WorkflowTransition>? permitted)
    {
        if (module.Workflow is not { } wf || !visibleBlocks.SelectMany(b => b.Fields).Any(f => f.Id == wf.StatusFieldId)) return null;
        var allowed = permitted ?? wf.Transitions;
        return new SchemaWorkflow(wf.StatusFieldKey, $"opt_{wf.InitialOptionId}",
            wf.States.Select(s => new SchemaWorkflowState($"opt_{s.OptionId}", s.ReadOnly)).ToList(),
            allowed.Select(t => new SchemaWorkflowTransition($"opt_{t.From}", $"opt_{t.To}")).ToList());
    }

    /// <summary>表頭與明細共用：我看得到的標題欄位 key 與區塊／欄位（excludeFieldId 是明細的表頭欄位）。</summary>
    private async Task<(string? TitleKey, List<SchemaBlock> Blocks)> BuildAsync(
        Module module, EffectivePermissions p, long? excludeFieldId, CancellationToken ct)
    {
        // 標題欄位對我隱藏時不外露它的 key
        var titleField = module.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == module.TitleFieldId);
        var titleKey = titleField is not null && p.CanSee(titleField) ? titleField.Key : null;
        var references = await ReferencesOfAsync(module, p, ct);
        var blocks = module.Blocks
            .OrderBy(b => b.Sequence).ThenBy(b => b.Id)
            .Select(b => new SchemaBlock(b.Id, b.LabelI18n, b.Collapsed, b.Columns,
                b.Fields.Where(f => f.Status == FieldStatus.Active && f.Id != excludeFieldId && p.CanSee(f))
                    .OrderBy(f => f.Sequence).ThenBy(f => f.Id)
                    .Select(f => ToSchemaField(f, p, references.GetValueOrDefault(f.Id))).ToList()))
            .Where(b => b.Fields.Count > 0)
            .ToList();
        return (titleKey, blocks);
    }

    /// <summary>
    /// 啟用中的明細模組（依 Key 排序）：權限由 detailPermissions 決定、欄位例外取自明細模組。
    /// 回傳 ETag 用的版本字串與併入明細權限的雜湊。
    /// </summary>
    private async Task<(List<SchemaDetail>? Details, string ETagVersion, string PermHash)> DetailsAsync(
        Module parent, EffectivePermissions parentPermissions,
        Func<Module, Task<EffectivePermissions?>> detailPermissions, CancellationToken ct)
    {
        var versions = new List<string> { parent.Version.ToString() };
        var hashes = new List<string> { parentPermissions.Hash };
        var details = new List<SchemaDetail>();
        foreach (var (listed, parentField) in (await modules.ListDetailModulesAsync(parent.Key, ct)).OrderBy(x => x.Child.Key, StringComparer.Ordinal))
        {
            if (!listed.IsReadable()) continue;
            var child = (await modules.GetByIdAsync(listed.Id, includeChildren: true, ct))!;
            if (await detailPermissions(child) is not { } p) continue;

            var (titleKey, blocks) = await BuildAsync(child, p, parentField.Id, ct);
            var fields = blocks.SelectMany(b => b.Fields).ToList();
            details.Add(new SchemaDetail(child.Key, child.NameI18n, parentField.Id, parentField.Key, titleKey,
                fields, await ToSchemaRulesAsync(child, blocks, ct)));
            versions.Add(child.Version.ToString());
            hashes.Add(p.Hash);
        }
        if (details.Count == 0) return (null, versions[0], hashes[0]);
        var combined = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", hashes))))[..8].ToLowerInvariant();
        return (details, string.Join(".", versions), combined);
    }

    /// <summary>
    /// 只送啟用中的規則（決策 11）：條件引用我看不到的欄位（或條件解析失敗）整條不送；
    /// 目標拿掉我看不到的欄位與區塊，除 * 外一個不剩就不送。
    /// </summary>
    private async Task<List<SchemaRule>> ToSchemaRulesAsync(Module module, List<SchemaBlock> visibleBlocks, CancellationToken ct)
    {
        var visibleFields = visibleBlocks.SelectMany(b => b.Fields).Select(f => f.Id).ToHashSet();
        var visibleBlockIds = visibleBlocks.Select(b => b.Id).ToHashSet();
        var userIds = await modules.ListUserIdsAsync(ct);
        var result = new List<SchemaRule>();
        foreach (var rule in module.Rules.Where(r => r.Active).OrderBy(r => r.Sequence).ThenBy(r => r.Id))
        {
            if (!RuleReferences.ConditionFieldIdsOf(rule.Condition).All(visibleFields.Contains)) continue;
            if (ConditionParser.Parse(rule.Condition, module, registry, ConditionUsage.Rule, userIds).Error is not null) continue;
            var targets = rule.Targets.Where(t => RuleTarget.TryParse(t, out var target) && target switch
            {
                RecordTarget => true,
                FieldTarget f => visibleFields.Contains(f.FieldId),
                BlockTarget b => visibleBlockIds.Contains(b.BlockId),
                _ => false
            }).ToList();
            if (targets.Count == 0) continue;
            result.Add(new SchemaRule(rule.Kind, rule.Condition, targets,
                rule.OptionIds.Select(id => $"opt_{id}").ToList(), rule.MessageI18n));
        }
        return result;
    }

    /// <summary>我看得到的參照欄位 → 目標名稱與 light 網址；目標已不存在的略過。</summary>
    private async Task<Dictionary<long, SchemaReference>> ReferencesOfAsync(Module module, EffectivePermissions p, CancellationToken ct)
    {
        var result = new Dictionary<long, SchemaReference>();
        var fields = module.Blocks.SelectMany(b => b.Fields)
            .Where(f => f is { Type: FieldType.Reference, Status: FieldStatus.Active, Config.RefTarget: not null } && p.CanSee(f));
        foreach (var f in fields)
        {
            var target = f.Config.RefTarget!;
            if (target.Kind == ReferenceTarget.ModuleKind)
            {
                var targetModule = await modules.GetByKeyAsync(target.Key, includeChildren: false, ct);
                if (targetModule is not null)
                    result[f.Id] = new SchemaReference(target.Kind, target.Key, targetModule.NameI18n, $"/modules/{target.Key}/records/light");
            }
            else if (providers.Get(target.Key) is { } provider)
                result[f.Id] = new SchemaReference(target.Kind, target.Key, provider.Name, $"/entities/{target.Key}/light");
        }
        return result;
    }

    /// <summary>唯讀欄位複製一份 Config 設 ReadOnly；選項 Active = 啟用且我能選（決策 3）。</summary>
    private static SchemaField ToSchemaField(Field f, EffectivePermissions p, SchemaReference? reference) =>
        new(f.Id, f.Key, f.Type, f.LabelI18n,
            p.CanWrite(f) || f.Config.ReadOnly ? f.Config : f.Config with { ReadOnly = true },
            f.Options.OrderBy(o => o.Sequence).ThenBy(o => o.Id)
                .Select(o => new SchemaOption($"opt_{o.Id}", o.LabelI18n, o.Color,
                    o.Active && !p.UnselectableOptionIds.Contains(o.Id))).ToList(),
            reference);
}
