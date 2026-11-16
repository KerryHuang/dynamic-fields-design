using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ConversionService.Data;

public record ConversionInput(
    string Key, I18nText Name, string SourceModuleKey, string TargetModuleKey, bool Active, ConversionDefinition Definition);

public record ConversionResponse(
    long Id, string Key, I18nText Name, string SourceModuleKey, string TargetModuleKey, bool Active, ConversionDefinition Definition);

/// <summary>轉換請求的一列：要轉的來源明細列與數量。</summary>
public record ConvertLineInput(long SourceLineId, decimal Quantity);

/// <summary>某來源記錄上一個轉換的可用狀況。Lines 為 null 是表頭轉換。</summary>
public record ConversionAvailability(
    string Key, I18nText Name, string TargetModuleKey, I18nText TargetName, bool Allowed, string? Reason,
    List<ConversionLineStatus>? Lines, List<ConvertedTarget> Targets);

/// <summary>Remaining = Quantity − Converted（即時計算；來源後來改量時可能是負數）。</summary>
public record ConversionLineStatus(long LineId, string Title, decimal Quantity, decimal Converted, decimal Remaining);

public record ConvertedTarget(long Id, string Title, string ModuleKey);

public static class ConversionMapper
{
    public static ConversionResponse ToResponse(this Conversion c, string sourceKey, string targetKey) =>
        new(c.Id, c.Key, c.NameI18n, sourceKey, targetKey, c.Active, c.Definition);
}
