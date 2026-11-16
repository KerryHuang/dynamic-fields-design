namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>自動編號流水號；Period 是 yyyyMM、yyyy 或空字串，NextValue 是下一個要發的號碼。</summary>
public class NumberSequence
{
    public long ModuleId { get; set; }
    public long FieldId { get; set; }
    public string Period { get; set; } = "";
    public long NextValue { get; set; }
}
