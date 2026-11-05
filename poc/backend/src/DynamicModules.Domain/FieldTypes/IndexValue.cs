namespace DynamicModules.Domain.FieldTypes;

/// <summary>寫入 Dynamic_RecordIndex 的一列值；Seq 從 0 起算（multienum 每個選項一列）。</summary>
public sealed record IndexValue(int Seq, string? TextValue, double? NumberValue, DateTime? DateValue, string? RefKey);
