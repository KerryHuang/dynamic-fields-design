namespace DynamicModules.Application.Common.Acting;

/// <summary>
/// 在一段非同步流程內覆寫目前使用者與營運時間（Demo 資料產生器用）。
/// 以 AsyncLocal 保存，平行的流程互不影響；Dispose 還原上一層，可以巢狀。
/// </summary>
public static class ActingScope
{
    private static readonly AsyncLocal<Frame?> Current = new();

    public static string? UserId => Current.Value?.UserId;

    public static DateTime? Now => Current.Value?.Now;

    /// <summary>now 為 null 時沿用外層的覆寫時間；外層也沒有就用真實時間。</summary>
    public static IDisposable Begin(string userId, DateTime? now = null)
    {
        var outer = Current.Value;
        var frame = new Frame(userId, now ?? outer?.Now, outer);
        Current.Value = frame;
        return frame;
    }

    private sealed class Frame(string userId, DateTime? now, Frame? outer) : IDisposable
    {
        public string UserId { get; } = userId;
        public DateTime? Now { get; } = now;

        public void Dispose() => Current.Value = outer;
    }
}
