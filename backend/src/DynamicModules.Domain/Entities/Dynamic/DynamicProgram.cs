using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>權限目錄項目（表 Dynamic_Program）；類名避免與 WebAPI 的 Program 撞名。</summary>
public class DynamicProgram
{
    public string ProgramCode { get; set; } = "";
    public I18nText NameI18n { get; set; } = new();
    public bool IsBeta { get; set; }
    public bool Active { get; set; }
}
