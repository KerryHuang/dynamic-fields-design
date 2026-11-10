using System.Text.Json;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>任務規則：模組的記錄符合觸發條件時產生一組任務。Key 全站唯一（套件裡是 taskRules[].code）。設計 §5。</summary>
public class TaskRule : AuditableEntity
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public I18nText NameI18n { get; set; } = new();
    public long ModuleId { get; set; }
    public long? AppId { get; set; }
    public bool Active { get; set; } = true;
    public TaskRuleRepeat Repeat { get; set; } = TaskRuleRepeat.Once;
    public TaskRuleTrigger Trigger { get; set; } = new();
    public string? WhenJson { get; set; }
    public List<TaskRuleTask> Tasks { get; set; } = [];
}

public enum TaskRuleRepeat { Once, EveryTime }

/// <summary>On：onCreate、onUpdateMatch、onFieldBecomes、manual。FieldId／Value 只用在 onFieldBecomes。</summary>
public record TaskRuleTrigger(string On, long? FieldId, JsonElement? Value)
{
    public TaskRuleTrigger() : this("onCreate", null, null) { }
}

/// <summary>規則裡的一個任務範本；標題與說明依觸發者語系解析後存進 TaskItem。</summary>
public class TaskRuleTask
{
    public long Id { get; set; }
    public long RuleId { get; set; }
    public string Code { get; set; } = "";
    public int SortOrder { get; set; }
    public I18nText TitleI18n { get; set; } = new();
    public I18nText? DescriptionI18n { get; set; }
    public long AssigneeFieldId { get; set; }
    public long? DueFieldId { get; set; }
    public int DueOffsetDays { get; set; }
    public string? WhenJson { get; set; }
}

/// <summary>寫入一筆代表「這筆記錄第 Occurrence 次觸發這條規則」。主鍵 (RuleId, RecordId, Occurrence)。</summary>
public class TaskRuleRun
{
    public long RuleId { get; set; }
    public long RecordId { get; set; }
    public int Occurrence { get; set; }
    public DateTime CreatedTime { get; set; }
    public string? TriggeredBy { get; set; }
}
