namespace DynamicModules.Domain.Repositories.Dynamic;

public interface ISystemSettingRepository
{
    /// <summary>設定值（不追蹤）；沒有這個鍵回 null。</summary>
    Task<string?> GetValueAsync(string key, CancellationToken ct);
    /// <summary>新增或覆寫，由呼叫端存檔。</summary>
    Task SetAsync(string key, string value, CancellationToken ct);
}
