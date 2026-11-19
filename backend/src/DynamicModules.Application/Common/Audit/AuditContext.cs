namespace DynamicModules.Application.Common.Audit;

/// <summary>
/// 一個請求（scope）的稽核補充資料（維運設計 §17.2.1）：handler 補目標的顯示名、新舊值與其他摘要項目，
/// <see cref="Behaviors.AuditBehavior{TReq,TRes}"/> 在請求成功時寫進稽核。
/// 只有最外層的稽核請求寫一列：巢狀的稽核請求（例如開應用程式時內部建群組、建事件）由外層那一列涵蓋，它們的補充資料被忽略。
/// </summary>
public sealed class AuditContext
{
    internal int Depth { get; set; }

    public string? TargetId { get; private set; }
    public string? TargetLabel { get; private set; }
    public AuditSummary? Summary { get; private set; }

    /// <summary>這次變更授予（true）或拿掉（false）系統管理員；非 null 時開安全事件（維運設計 §17.2.2、O2-U11）。</summary>
    public bool? AdminChanged { get; private set; }

    /// <summary>這次變更讓有人取得（true）或只有收回（false）設計器或權限設定；非 null 時開 privilege.changed（權限軸設計 §9）。</summary>
    public bool? PrivilegeChanged { get; private set; }

    private bool Outermost => Depth <= 1;

    /// <summary>補目標：建立後才知道 id 的、或要記顯示名快照的。</summary>
    public void Target(string? id = null, string? label = null)
    {
        if (!Outermost) return;
        if (id is not null) TargetId = id;
        if (label is not null) TargetLabel = label;
    }

    /// <summary>新舊值；一定要補的見 §17.2.1（權限、群組成員、使用者、政策…）。</summary>
    public void Describe(object? before, object? after)
    {
        if (Outermost) (Summary ??= new AuditSummary()).Diff(before, after);
    }

    /// <summary>其他摘要項目（變更種類、變動的格子…）。</summary>
    public void Detail(string key, object? value)
    {
        if (Outermost) (Summary ??= new AuditSummary()).Add(key, value);
    }

    /// <summary>handler 標記系統管理員旗標有變；<see cref="Behaviors.AuditBehavior{TReq,TRes}"/> 寫完稽核列後在同一交易開安全事件。</summary>
    public void MarkAdminChanged(bool granted)
    {
        if (Outermost) AdminChanged = granted;
    }

    /// <summary>handler 比對持有者快照後標記；<see cref="Behaviors.AuditBehavior{TReq,TRes}"/> 寫完稽核列後在同一交易開安全事件。</summary>
    public void MarkPrivilegeChanged(bool granted)
    {
        if (Outermost) PrivilegeChanged = granted;
    }

    internal void Reset()
    {
        AdminChanged = null;
        PrivilegeChanged = null;
        TargetId = null;
        TargetLabel = null;
        Summary = null;
    }
}
