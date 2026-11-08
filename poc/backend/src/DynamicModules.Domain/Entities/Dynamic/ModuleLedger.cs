namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>不可修改分錄：一個模組一列。記錄建立後不能改、不能刪，只能新增沖銷分錄（設計 §6）。</summary>
public class ModuleLedger : AuditableEntity
{
    public long Id { get; set; }
    public long ModuleId { get; set; }
    public LedgerDefinition Definition { get; set; } = new();
}

/// <summary>ReversalFieldId：本模組指向自己的唯讀參照欄位；Negate：沖銷時取負號；Swap：沖銷時互換值的欄位對。</summary>
public record LedgerDefinition(long ReversalFieldId, List<long> Negate, List<List<long>> Swap)
{
    public LedgerDefinition() : this(0, [], []) { }
}

/// <summary>沖銷關係；主鍵是原分錄，所以同一筆不可能被沖銷兩次。</summary>
public class LedgerReversal
{
    public long OriginalRecordId { get; set; }
    public long ReversalRecordId { get; set; }
    public DateTime CreatedTime { get; set; }
}

/// <summary>請求層冪等紀錄（設計 §3.2）；主鍵 (UserId, Key)。</summary>
public class OperationRecord
{
    public string UserId { get; set; } = "";
    public string Key { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public int ResultKind { get; set; }
    public string ResultJson { get; set; } = "";
    public DateTime CreatedTime { get; set; }
}
