using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.RuleService.Data;

public record RuleInput(
    RuleKind Kind, JsonElement Condition, List<string> Targets, List<long>? OptionIds, I18nText? Message, bool Active);

public record RuleResponse(
    long Id, RuleKind Kind, JsonElement Condition, List<string> Targets, List<long> OptionIds, I18nText? Message, bool Active);

public static class RuleMappings
{
    public static RuleResponse ToResponse(this Rule r) =>
        new(r.Id, r.Kind, r.Condition, r.Targets, r.OptionIds, r.MessageI18n, r.Active);

    public static List<RuleResponse> ToResponse(this IEnumerable<Rule> rules) =>
        rules.OrderBy(r => r.Sequence).Select(r => r.ToResponse()).ToList();
}
