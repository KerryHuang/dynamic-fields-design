namespace DynamicModules.Domain.Repositories.Dynamic;

public interface ISystemSettingRepository
{
    /// <summary>設定值（不追蹤）；沒有這個鍵回 null。</summary>
    Task<string?> GetValueAsync(string key, CancellationToken ct);
    /// <summary>新增或覆寫，由呼叫端存檔。</summary>
    Task SetAsync(string key, string value, CancellationToken ct);
    /// <summary>鍵以 prefix 開頭（Ordinal）的所有設定，不追蹤。</summary>
    Task<IReadOnlyDictionary<string, string>> ListByPrefixAsync(string prefix, CancellationToken ct);
    /// <summary>刪除這個鍵；沒有這筆就什麼都不做。由呼叫端存檔。</summary>
    Task RemoveAsync(string key, CancellationToken ct);
}
