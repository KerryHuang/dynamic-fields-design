namespace DynamicModules.Application.Dynamic.ImportService.Data;

/// <summary>表頭一欄的對應結果：FieldKey 有值表示這欄會匯入；略過時 FieldKey 為 null、IgnoredReason 說明原因。</summary>
public record ImportColumn(string Header, string? FieldKey, string? IgnoredReason);

/// <summary>一列的錯誤；Line 是檔案裡的行號（表頭是第 1 行），鍵是欄位 key 或 _record。</summary>
public record ImportRowError(int Line, Dictionary<string, string[]> Errors);

/// <summary>預覽：只列有錯的列。</summary>
public record ImportPreviewResponse(List<ImportColumn> Columns, int Total, int Valid, List<ImportRowError> Errors);

public record ImportResultResponse(int Created);
