using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.EventService;

public record EventInput(string Key, I18nText Name, string SourceModuleKey, bool Active, EventDefinition Definition);

public record EventResponse(
    string Key, I18nText Name, string SourceModuleKey, bool Active, EventDefinition Definition, IReadOnlyList<string> Warnings);

public static class EventMapping
{
    public static EventResponse ToResponse(this ModuleEvent e, string sourceModuleKey, IReadOnlyList<string>? warnings = null) =>
        new(e.Key, e.NameI18n, sourceModuleKey, e.Active, e.Definition, warnings ?? []);
}
