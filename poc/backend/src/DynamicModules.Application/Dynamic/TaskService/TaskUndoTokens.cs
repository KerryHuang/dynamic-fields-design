namespace DynamicModules.Application.Dynamic.TaskService;

/// <summary>
/// 復原重派的憑證內容：只限發出它的人（ActorUserId）、只限這個任務、任務版本沒變、到期前（Expires，營運時間）有效。
/// 憑證無狀態；只能用一次靠版本：復原一寫入，任務版本就變了。
/// </summary>
public sealed record TaskUndoTicket(long TaskId, string ActorUserId, string? FromUserId, string? ToUserId, string Version, DateTime Expires);

/// <summary>發出與讀回憑證；Read 對竄改、格式不對、無法解密的字串回 null（不丟例外）。實作用加密保證不能偽造。</summary>
public interface ITaskUndoTokens
{
    string Issue(TaskUndoTicket ticket);
    TaskUndoTicket? Read(string token);
}
