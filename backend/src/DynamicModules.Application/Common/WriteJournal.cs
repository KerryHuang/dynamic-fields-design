namespace DynamicModules.Application.Common;

public enum JournalKind { Added, Modified }

/// <summary>
/// 一次 SaveChanges 對一筆記錄造成的增量。EF 的 OriginalValues 是「上一次存進資料庫的值」，
/// 所以每一項本身就是這一次儲存的精確增量（設計 §3.3）。Seq 是請求內的流水序號。
/// </summary>
public sealed record JournalEntry(
    int Seq, long RecordId, long ModuleId, JournalKind Kind,
    string? BeforeValues, bool BeforeDeleted, string AfterValues, bool AfterDeleted,
    bool Silent, string OwnerUserId);

/// <summary>
/// 請求內的寫入日誌：由 UnitOfWork 在每次 SaveChanges 時從 EF change tracker 掃出，所以任何寫入路徑
/// （彙總重算、明細、匯入、轉換）都自動進日誌，不必在每個寫入點登記。每個 scope（一個請求）一份。
/// </summary>
public interface IWriteJournal
{
    IReadOnlyList<JournalEntry> Entries { get; }

    /// <summary>
    /// 「輸入界線」：Seq 小於等於它的項目是這次請求的 handler 寫入（使用者輸入，含整合、匯入的直接寫入），之後的是事件效果。
    /// 由 <c>RecordEffectsBehavior</c> 在跑寫入後階段前呼叫 <see cref="MarkInputEnd"/> 設定；簽核鎖定以它與「第一個轉出鎖定狀態的項目」取較大者當解鎖界線（設計 §9.3）。
    /// </summary>
    int InputSeqEnd { get; }

    void MarkInputEnd();

    /// <summary>由 UnitOfWork 呼叫。</summary>
    void Add(Func<int, JournalEntry> create);

    /// <summary>標記這個物件的下一次寫入為「靜默」：仍列入驗證，但不觸發事件（彙總重算用，設計 §4.5）。</summary>
    void MarkSilent(object trackedRecord);

    /// <summary>這個物件是否被標為靜默。只看，不消耗。</summary>
    bool IsMarkedSilent(object trackedRecord);

    /// <summary>清空日誌與靜默標記：同一個 scope 連續送多個獨立請求時（Demo 產生器），每個請求開始前呼叫，後面的階段才不會重播前一個請求的項目。</summary>
    void Clear();

    /// <summary>標記只管「下一次」寫入：該次儲存成功後由 UnitOfWork 呼叫，之後同一物件的寫入恢復正常觸發。</summary>
    void ClearSilent(object trackedRecord);
}

public sealed class WriteJournal : IWriteJournal
{
    private readonly List<JournalEntry> _entries = [];
    private readonly HashSet<object> _silent = new(ReferenceEqualityComparer.Instance);

    public IReadOnlyList<JournalEntry> Entries => _entries;

    public int InputSeqEnd { get; private set; }

    public void MarkInputEnd() => InputSeqEnd = _entries.Count;

    public void Add(Func<int, JournalEntry> create) => _entries.Add(create(_entries.Count + 1));

    public void MarkSilent(object trackedRecord) => _silent.Add(trackedRecord);

    public bool IsMarkedSilent(object trackedRecord) => _silent.Contains(trackedRecord);

    public void ClearSilent(object trackedRecord) => _silent.Remove(trackedRecord);

    public void Clear()
    {
        _entries.Clear();
        _silent.Clear();
        InputSeqEnd = 0;
    }
}

public static class JournalView
{
    /// <summary>
    /// 同一筆記錄的多個項目合併：寫入前取第一項、寫入後取最後一項、種類以第一項為準；
    /// 只有每一項都靜默才算靜默。回傳依第一次出現的順序排列。
    /// </summary>
    public static IReadOnlyList<JournalEntry> Merge(IEnumerable<JournalEntry> entries) =>
        entries.GroupBy(e => e.RecordId).Select(g =>
        {
            var first = g.First();
            var last = g.Last();
            return first with
            {
                AfterValues = last.AfterValues, AfterDeleted = last.AfterDeleted, OwnerUserId = last.OwnerUserId,
                Silent = g.All(x => x.Silent)
            };
        }).OrderBy(e => e.Seq).ToList();
}
