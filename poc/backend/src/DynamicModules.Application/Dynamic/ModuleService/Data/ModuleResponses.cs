using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ModuleService.Data;

public record ModuleResponse(
    long Id, string Key, string ProgramCode, I18nText Name, string? Icon, string? Section,
    long? TitleFieldId, string Status, int Version, ModuleKind Kind, string? HostEntityKey, string? DetailOf = null);

public record ModuleDetailResponse(
    long Id, string Key, string ProgramCode, I18nText Name, string? Icon, string? Section,
    long? TitleFieldId, string Status, int Version, List<BlockResponse> Blocks,
    ModuleKind Kind, string? HostEntityKey, string? DetailOf = null);

public record BlockResponse(long Id, I18nText Label, int Sequence, bool Collapsed, int Columns, List<FieldResponse> Fields);

public record FieldResponse(
    long Id, string Key, FieldType Type, I18nText Label, FieldConfig Config, long BlockId,
    int Sequence, string Status, List<OptionResponse> Options);

/// <summary>Value 是寫進記錄 JSON 的字串 opt_&lt;id&gt;。</summary>
public record OptionResponse(long Id, string Value, I18nText Label, string? Color, int Sequence, bool Active);

public static class ModuleMapper
{
    /// <param name="detailOf">模組沒載入欄位時（清單）由呼叫端提供表頭模組鍵；null 則從已載入的欄位推得。</param>
    public static ModuleResponse ToResponse(this Module m, string? detailOf = null) =>
        new(m.Id, m.Key, m.ProgramCode, m.NameI18n, m.Icon, m.Section, m.TitleFieldId, m.Status.ToString(), m.Version, m.Kind, m.HostEntityKey,
            detailOf ?? ModuleRelations.ParentKeyOf(m));

    public static ModuleDetailResponse ToDetail(this Module m) =>
        new(m.Id, m.Key, m.ProgramCode, m.NameI18n, m.Icon, m.Section, m.TitleFieldId, m.Status.ToString(), m.Version,
            m.Blocks.OrderBy(b => b.Sequence).ThenBy(b => b.Id).Select(ToResponse).ToList(), m.Kind, m.HostEntityKey,
            ModuleRelations.ParentKeyOf(m));

    public static BlockResponse ToResponse(this Block b) =>
        new(b.Id, b.LabelI18n, b.Sequence, b.Collapsed, b.Columns,
            b.Fields.OrderBy(f => f.Sequence).ThenBy(f => f.Id).Select(ToResponse).ToList());

    public static FieldResponse ToResponse(this Field f) =>
        new(f.Id, f.Key, f.Type, f.LabelI18n, f.Config, f.BlockId, f.Sequence, f.Status.ToString(),
            f.Options.OrderBy(o => o.Sequence).ThenBy(o => o.Id).Select(ToResponse).ToList());

    public static OptionResponse ToResponse(this FieldOption o) =>
        new(o.Id, $"opt_{o.Id}", o.LabelI18n, o.Color, o.Sequence, o.Active);
}
