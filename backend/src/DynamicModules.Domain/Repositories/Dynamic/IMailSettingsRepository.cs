using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>核心寄信設定（<c>Dynamic_MailSettings</c>，單列 <see cref="MailSettings.Default"/>）。</summary>
public interface IMailSettingsRepository
{
    /// <summary>不追蹤；還沒設定過回 null。</summary>
    Task<MailSettings?> GetAsync(CancellationToken ct);
    Task<MailSettings?> GetTrackedAsync(CancellationToken ct);
    void Add(MailSettings settings);
}
