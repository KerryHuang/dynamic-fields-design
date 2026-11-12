namespace DynamicModules.Application.Common.Uploads;

/// <summary>
/// 伺服器內部程式（目前只有 Demo 產生器）寫入附件時略過環境的上傳開關；權限、副檔名、大小照常檢查。
/// 不能從 HTTP 開啟：只有程式碼呼叫 <see cref="Begin"/> 才會生效，旗標存在 AsyncLocal，不會流到其他請求。
/// </summary>
public static class ServerUploadScope
{
    private static readonly AsyncLocal<bool> Flag = new();

    public static bool Active => Flag.Value;

    /// <summary>進入範圍；Dispose 還原成進入前的值（巢狀時還原成外層）。</summary>
    public static IDisposable Begin()
    {
        var previous = Flag.Value;
        Flag.Value = true;
        return new Restore(previous);
    }

    private sealed class Restore(bool previous) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            Flag.Value = previous;
        }
    }
}
