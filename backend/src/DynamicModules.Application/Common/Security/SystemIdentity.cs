namespace DynamicModules.Application.Common.Security;

/// <summary>
/// 事件效果的系統身分（設計 §4.2）：進入期間，存取守衛對目標模組回傳完整權限（不看動作權限、資料範圍、欄位隱藏與唯讀），
/// 新增的記錄擁有者改為指定的人（來源記錄的擁有者）。稽核欄位仍記觸發的使用者。每個請求（scope）一份，可巢狀。
/// </summary>
public interface ISystemIdentity
{
    bool Active { get; }
    string? OwnerUserId { get; }
    IDisposable Enter(string? ownerUserId);
}

public sealed class SystemIdentity : ISystemIdentity
{
    private readonly Stack<string?> _owners = new();

    public bool Active => _owners.Count > 0;
    public string? OwnerUserId => _owners.Count > 0 ? _owners.Peek() : null;

    public IDisposable Enter(string? ownerUserId)
    {
        _owners.Push(ownerUserId);
        return new Scope(_owners);
    }

    private sealed class Scope(Stack<string?> owners) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            owners.Pop();
        }
    }
}
