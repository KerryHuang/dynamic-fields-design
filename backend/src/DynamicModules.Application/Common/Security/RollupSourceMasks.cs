using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Common.Security;

/// <summary>
/// 彙總來源的遮罩輸入（給 <see cref="PermissionResolver.Resolve"/>）：
/// <see cref="HiddenSourceFieldIds"/> 是使用者在各彙總來源模組看不到的欄位；<see cref="ConditionRefs"/>
/// 是每個有加總對象或條件的彙總、以正式 <see cref="ConditionParser"/> 解析出的條件欄位（沒有條件是空集合；來源模組找不到或條件解析失敗是 null＝隱藏）。
/// </summary>
public sealed record RollupSourceMask(IReadOnlySet<long> HiddenSourceFieldIds, IReadOnlyDictionary<long, IReadOnlySet<long>?> ConditionRefs);

/// <summary>
/// 依彙總實際的來源模組算遮罩（mcp1a 第五輪審查必修 7、9）。明細來源：動作軸取表頭、欄位例外取明細；一般參照來源用來源模組自己的權限。
/// 加總對象與彙總條件都看；畫面通道與 MCP 通道相同（#74、#75，使用者 2026-11-21 裁決，取代 mcp1a 計畫 Q3「畫面通道不改」）。
/// </summary>
public sealed class RollupSourceMasks(IPermissionRepository permissions, IModuleRepository modules, FieldTypeRegistry registry)
{
    /// <summary>沒有需要看來源的彙總回 (true, null)；任一來源模組讀不到權限輸入回 (false, null)，呼叫端不放行。</summary>
    public async Task<(bool Ok, RollupSourceMask? Mask)> LoadAsync(Module module, PermissionInputs inputs, string userId, CancellationToken ct)
    {
        var rollups = module.Blocks.SelectMany(b => b.Fields)
            .Where(f => f is { Type: FieldType.Rollup, Config.Rollup: { } r } && (r.AggregateFieldId is not null || r.Condition is not null))
            .ToList();
        if (rollups.Count == 0) return (true, null);

        var hidden = new HashSet<long>();
        var conditionRefs = new Dictionary<long, IReadOnlySet<long>?>();
        var sources = new Dictionary<long, Module?>();
        var resolved = new HashSet<(long, bool)>();
        HashSet<string>? userIds = null;
        foreach (var f in rollups)
        {
            var cfg = f.Config.Rollup!;
            if (!sources.TryGetValue(cfg.SourceFieldId, out var source))
                sources[cfg.SourceFieldId] = source = await modules.GetByFieldIdAsync(cfg.SourceFieldId, ct);
            var sourceField = source?.Blocks.SelectMany(b => b.Fields).FirstOrDefault(x => x.Id == cfg.SourceFieldId);
            if (source is null || sourceField is null)
            {
                conditionRefs[f.Id] = null;
                continue;
            }
            var detail = sourceField.Config.Relation == ReferenceRelation.Detail;

            if (resolved.Add((source.Id, detail)))
            {
                var sourceInputs = await permissions.LoadInputsAsync(userId, source, ct);
                if (sourceInputs is null) return (false, null);
                hidden.UnionWith((detail
                    ? PermissionResolver.Resolve(source, DetailInputs(inputs, sourceInputs), module)
                    : PermissionResolver.Resolve(source, sourceInputs)).HiddenFieldIds);
            }

            if (cfg.Condition is not { } condition)
            {
                conditionRefs[f.Id] = new HashSet<long>();
                continue;
            }
            // 與 RollupCalculator 同一個 parser：它解析失敗就略過條件、算全部來源列，所以這裡解析失敗（含語意錯誤）一律隱藏
            userIds ??= await modules.ListUserIdsAsync(ct);
            var parsed = ConditionParser.Parse(condition, source, registry, ConditionUsage.Rule, userIds);
            conditionRefs[f.Id] = parsed.Error is null ? FieldIdsOf(parsed.Node) : null;
        }
        return (true, new RollupSourceMask(hidden, conditionRefs));
    }

    private static PermissionInputs DetailInputs(PermissionInputs parentInputs, PermissionInputs childInputs) =>
        parentInputs with { FieldPermissions = childInputs.FieldPermissions, OptionScopes = childInputs.OptionScopes, FieldGrants = childInputs.FieldGrants };

    private static HashSet<long> FieldIdsOf(ConditionNode? node)
    {
        var ids = new HashSet<long>();
        void Walk(ConditionNode? n)
        {
            switch (n)
            {
                case ConditionGroup g:
                    foreach (var c in g.Children) Walk(c);
                    break;
                case FieldCondition c:
                    ids.Add(c.Field.Id);
                    if (c.RefField is { } r) ids.Add(r.Id);
                    break;
            }
        }
        Walk(node);
        return ids;
    }
}
