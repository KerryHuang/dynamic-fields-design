using DynamicModules.Application.Common;

namespace DynamicModules.Persistence;

/// <summary>
/// 同一個行程內，寫入交易先進先出排隊，一次只放一個進 SQLite。
/// SQLite 的 busy handler 只是睡眠後重試，沒有佇列：寫入壓力下運氣差的請求會一直輸給剛到的請求
/// （實測 20 人只寫時，有人 20 秒只完成 1 次、最大等待 20 秒，等滿 30 秒就丟 database is locked）。
/// 排隊後每個請求的等待約等於前面的人數乘以每筆的持鎖時間，有上限，也有明確的逾時。
/// 寫入鎖仍由 SQLite 保證（BEGIN IMMEDIATE），所以多行程共用同一個檔案時仍然正確，只是不保證公平。
/// </summary>
public sealed class WriteGate
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, WriteGate> ByFile = new();

    private readonly object _sync = new();
    private readonly LinkedList<TaskCompletionSource<bool>> _waiters = new();
    private bool _held;

    /// <summary>檔案型資料庫每個檔案一個閘；記憶體資料庫只有一條連線，不會有寫入者競爭，回 null。</summary>
    public static WriteGate? For(string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString)) return null;
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
        var source = builder.DataSource;
        if (string.IsNullOrEmpty(source) || source == ":memory:" || source.StartsWith("file:")
            || builder.Mode == Microsoft.Data.Sqlite.SqliteOpenMode.Memory)
            return null;
        return ByFile.GetOrAdd(Path.GetFullPath(source), _ => new WriteGate());
    }

    /// <summary>CancelAfter 接受的最長時間（約 24.8 天）；更長的逾時夾到這個值。</summary>
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>連線字串的 Default Timeout（秒，預設 30）：SQLite 等鎖的上限，也當作排隊的上限。0 在 SQLite 是不設上限。</summary>
    public static TimeSpan TimeoutOf(string? connectionString)
    {
        var seconds = string.IsNullOrEmpty(connectionString)
            ? 30
            : new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DefaultTimeout;
        return seconds <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// 取得寫入權；回傳的物件 Dispose 時放開。逾時丟 <see cref="DatabaseBusyException"/>，呼叫端取消則維持取消。
    /// 逾時 ≤ 0 或無限視為不設上限，超過計時器上限的夾到上限。
    /// </summary>
    public async Task<IDisposable> EnterAsync(TimeSpan timeout, CancellationToken ct)
    {
        LinkedListNode<TaskCompletionSource<bool>> node;
        lock (_sync)
        {
            if (!_held)
            {
                _held = true;
                return new Releaser(this);
            }
            node = _waiters.AddLast(new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        // 節點已經排進佇列：從這裡起任何例外都必須讓出節點，否則 Release 會把寫入權交給沒人等的節點，之後所有寫入都卡住
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeout > TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan) linked.CancelAfter(timeout > MaxTimeout ? MaxTimeout : timeout);
            using var registration = linked.Token.Register(() => node.Value.TrySetCanceled());
            await node.Value.Task;
            return new Releaser(this);
        }
        catch (OperationCanceledException)
        {
            Abandon(node);
            if (ct.IsCancellationRequested) throw;
            throw new DatabaseBusyException(DatabaseBusyException.DefaultMessage);
        }
        catch
        {
            Abandon(node);
            throw;
        }
    }

    /// <summary>放棄排隊。節點還在佇列就移除；已經被交付寫入權（來不及回傳）就把權放開。</summary>
    private void Abandon(LinkedListNode<TaskCompletionSource<bool>> node)
    {
        var granted = false;
        lock (_sync)
        {
            if (node.List is not null) _waiters.Remove(node);
            else granted = node.Value.Task.IsCompletedSuccessfully;
        }
        if (granted) Release();
    }

    private void Release()
    {
        lock (_sync)
        {
            // 把寫入權直接交給排最前面、還沒放棄的人；沒有人在等才放開
            while (_waiters.First is { } first)
            {
                _waiters.RemoveFirst();
                if (first.Value.TrySetResult(true)) return;
            }
            _held = false;
        }
    }

    private sealed class Releaser(WriteGate gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}
