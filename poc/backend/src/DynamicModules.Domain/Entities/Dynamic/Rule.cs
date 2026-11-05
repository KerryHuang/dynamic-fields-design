using System.Text.Json;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>模組規則；Targets 元素是 field:&lt;id&gt;、block:&lt;id&gt; 或 *。</summary>
public class Rule : AuditableEntity
{
    public long Id { get; set; }
    public long ModuleId { get; set; }
    public RuleKind Kind { get; set; }
    public JsonElement Condition { get; set; }
    public List<string> Targets { get; set; } = [];
    /// <summary>只有 OptionFilter 使用：目標欄位剩下的選項。</summary>
    public List<long> OptionIds { get; set; } = [];
    public I18nText? MessageI18n { get; set; }
    public int Sequence { get; set; }
    public bool Active { get; set; } = true;
}
