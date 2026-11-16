using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.References;

public record LightItem(string Key, string Title);

public record LightPage(IReadOnlyList<LightItem> Items, int Total);

/// <summary>既有實體（外部系統的既有表）的參照來源。</summary>
public interface IReferenceProvider
{
    string EntityKey { get; }
    I18nText Name { get; }
    Task<LightPage> LightAsync(string? keyword, int page, int size, CancellationToken ct);

    /// <summary>只回存在且未刪除的鍵。</summary>
    Task<IReadOnlyDictionary<string, string>> TitlesAsync(IReadOnlyCollection<string> keys, CancellationToken ct);
}
