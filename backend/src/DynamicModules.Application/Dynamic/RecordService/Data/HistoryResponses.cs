using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.RecordService.Data;

/// <summary>歷程的一列（已定決策 3）；Operation 是 create／update／delete，Detail 只有明細列的歷程才有。</summary>
public record RecordHistoryResponse(
    long Id, string Operation, string? FieldKey, I18nText? FieldLabel, string? OldText, string? NewText,
    string ChangedUserId, string ChangedUserName, DateTime ChangedTime, HistoryDetailRow? Detail);

/// <summary>歷程所屬的明細列；Title 在讀者看不到明細標題欄位時為空字串。</summary>
public record HistoryDetailRow(string ModuleKey, I18nText ModuleName, long RecordId, string Title);
