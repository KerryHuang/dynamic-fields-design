namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>自助報表；定義存成 DefinitionJson（格式見 ReportDefinition），擁有者之外可分享給群組。</summary>
public class Report : AuditableEntity
{
    public long Id { get; set; }
    public string OwnerUserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string DefinitionJson { get; set; } = "{}";
    /// <summary>GUID 字串，每次寫入都換新值。</summary>
    public string RowVersion { get; set; } = "";
    public List<ReportShare> Shares { get; set; } = [];
}
