namespace DynamicModules.Domain.ValueObjects;

/// <summary>檢視的排序項；Field 是 Field.Id，Order 是 asc 或 desc。</summary>
public record ViewSort(long Field, string Order);
