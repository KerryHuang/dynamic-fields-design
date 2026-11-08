namespace DynamicModules.Application.Common;

/// <summary>
/// 一個寫入請求的操作識別（設計 §3.1）。客戶端帶 Idempotency-Key 就用它，沒帶則伺服器產生一個 GUID；
/// 事件執行時所有串聯寫入共用同一個值，寫進事件事實表，一個請求造成的連動都查得到。
/// </summary>
public interface IOperationContext
{
    string OperationId { get; }

    /// <summary>客戶端帶的 Idempotency-Key（已驗格式）；沒帶為 null，此時不做請求層冪等。</summary>
    string? ClientKey { get; set; }

    /// <summary>這個請求是不是回放先前成功的結果（WebAPI 據此加 Idempotent-Replay 標頭）。</summary>
    bool Replayed { get; set; }
}

public sealed class OperationContext : IOperationContext
{
    private readonly string _generated = Guid.NewGuid().ToString("N");

    public string OperationId => ClientKey ?? _generated;
    public string? ClientKey { get; set; }
    public bool Replayed { get; set; }

    public const string KeyPattern = "^[A-Za-z0-9_-]{8,64}$";
}
