namespace DynamicModules.Application.Dynamic.AttachmentService;

/// <summary>
/// 附件的檔案儲存。介面只做目前需要的：存、讀、刪、列。儲存名一律是系統產生的 32 碼十六進位 id，
/// 實作要自己再驗一次格式，不信任呼叫端。
/// </summary>
public interface IAttachmentStore
{
    /// <summary>寫入；超過 maxBytes 就中止、刪掉寫到一半的檔案並回 null，否則回實際大小。</summary>
    Task<long?> SaveAsync(string id, Stream content, long maxBytes, CancellationToken ct);

    /// <summary>檔案不存在或 id 格式不對回 null。</summary>
    Stream? OpenRead(string id);

    bool Delete(string id);

    IReadOnlyList<StoredAttachment> List();
}

public sealed record StoredAttachment(string Id, DateTime LastWriteUtc);
