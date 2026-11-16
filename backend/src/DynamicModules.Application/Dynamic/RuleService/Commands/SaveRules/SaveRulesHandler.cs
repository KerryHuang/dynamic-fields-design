using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.FieldService.Data;
using DynamicModules.Application.Dynamic.RuleService.Data;
using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Rules;
using MediatR;

namespace DynamicModules.Application.Dynamic.RuleService.Commands.SaveRules;

public class SaveRulesHandler(IModuleRepository modules, IUnitOfWork uow, FieldTypeRegistry registry,
    ICurrentUser user, IBusinessTime time)
    : IRequestHandler<SaveRulesRequest, ServiceResult>
{
    private const int MaxRules = 100;
    private const string BadTargets = "規則目標不正確";

    public async Task<ServiceResult> Handle(SaveRulesRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound(MessageRef.Of(MessageText.ModuleNotFound));

        var input = request.Rules;
        if (input.Count > MaxRules) return FieldRules.Invalid("rules", "規則最多 100 條");

        var userIds = await modules.ListUserIdsAsync(ct);
        var errors = new Dictionary<string, string[]>();
        for (var i = 0; i < input.Count; i++) Check(module, input[i], i, userIds, errors);
        if (errors.Count > 0) return ServiceResult.Invalid(errors);

        var now = time.Now;
        module.Rules.Clear();
        for (var i = 0; i < input.Count; i++)
        {
            var r = input[i];
            var rule = new Rule
            {
                Kind = r.Kind,
                Condition = r.Condition.Clone(),
                Targets = [.. r.Targets],
                OptionIds = r.Kind == RuleKind.OptionFilter ? (r.OptionIds ?? []).Distinct().ToList() : [],
                MessageI18n = r.Kind == RuleKind.Validation ? r.Message : null,
                Sequence = i + 1,
                Active = r.Active
            };
            rule.StampCreated(user.UserId, now);
            module.Rules.Add(rule);
        }

        module.TouchVersion(user.UserId, now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(module.Rules.ToResponse());
    }

    private void Check(Module module, RuleInput r, int i, IReadOnlySet<string> userIds, Dictionary<string, string[]> errors)
    {
        void Err(string prop, string message) => errors.TryAdd($"rules[{i}].{prop}", [message]);

        if (!CheckTargets(module, r, out var optionTarget)) Err("targets", BadTargets);

        if (r.Condition.ValueKind != System.Text.Json.JsonValueKind.Object)
            Err("condition", "規則條件不正確：篩選條件格式錯誤");
        else
        {
            var parsed = ConditionParser.Parse(r.Condition, module, registry, ConditionUsage.Rule, userIds);
            if (parsed.Error is not null) Err("condition", $"規則條件不正確：{parsed.Error}");
        }

        if (r.Kind == RuleKind.Validation && (r.Message is null || !r.Message.IsValid(out _)))
            Err("message", "驗證規則必須有訊息");

        if (r.Kind == RuleKind.OptionFilter && optionTarget is not null
            && (r.OptionIds ?? []).Any(id => optionTarget.Options.All(o => o.Id != id)))
            Err("optionIds", "選項不屬於目標欄位");
    }

    /// <summary>檢查目標；OptionFilter 通過時 optionTarget 是它唯一的目標欄位。</summary>
    private static bool CheckTargets(Module module, RuleInput r, out Field? optionTarget)
    {
        optionTarget = null;
        if (r.Targets is not { Count: > 0 } || r.Targets.Distinct().Count() != r.Targets.Count) return false;

        var parsed = new List<RuleTarget>();
        foreach (var t in r.Targets)
        {
            if (!RuleTarget.TryParse(t, out var target)) return false;
            parsed.Add(target);
        }

        var fields = module.Blocks.SelectMany(b => b.Fields).Where(f => f.Status == FieldStatus.Active).ToList();
        var blockIds = module.Blocks.Select(b => b.Id).ToHashSet();
        var resolved = new List<Field>();
        foreach (var target in parsed)
        {
            switch (target)
            {
                case FieldTarget ft:
                    if (fields.FirstOrDefault(f => f.Id == ft.FieldId) is not { } f) return false;
                    resolved.Add(f);
                    break;
                case BlockTarget bt when !blockIds.Contains(bt.BlockId):
                    return false;
            }
        }

        switch (r.Kind)
        {
            case RuleKind.Visibility:
                return parsed.All(t => t is not RecordTarget);
            case RuleKind.ReadOnly:
                return true;
            case RuleKind.OptionFilter:
                if (resolved.Count != 1 || parsed.Count != 1 || resolved[0].Type is not (FieldType.Enum or FieldType.MultiEnum))
                    return false;
                optionTarget = resolved[0];
                return true;
            default: // Required、Validation 只能指向欄位
                return parsed.All(t => t is FieldTarget);
        }
    }
}
