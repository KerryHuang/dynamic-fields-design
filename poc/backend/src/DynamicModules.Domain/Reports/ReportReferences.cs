using System.Text.Json;

namespace DynamicModules.Domain.Reports;

/// <summary>報表定義引用的模組與欄位，供刪除前的引用盤點使用。</summary>
public static class ReportReferences
{
    /// <summary>定義 JSON 無法解析時回 null，呼叫端要當成「引用了一切」（fail-closed）。</summary>
    public static ReportRefs? TryOf(string definitionJson)
    {
        try
        {
            return Of(ReportDefinition.Parse(definitionJson));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ReportRefs Of(ReportDefinition d)
    {
        var modules = new HashSet<string> { d.MainModuleKey };
        var fields = new HashSet<long>();
        void Add(ReportFieldRef? r)
        {
            if (r is null) return;
            fields.Add(r.FieldId);
            if (r.ViaFieldId is { } via) fields.Add(via);
        }
        if (d.Child is { } child)
        {
            modules.Add(child.ModuleKey);
            fields.Add(child.LinkFieldId);
        }
        foreach (var c in d.Columns ?? []) Add(c);
        foreach (var g in d.Groups ?? []) Add(g.Field);
        foreach (var m in d.Measures ?? [])
        {
            Add(m.Field);
            Add(m.Condition?.Field);
        }
        foreach (var f in d.Filters ?? []) Add(f.Field);
        Add(d.PrimaryDateField);
        return new ReportRefs(modules, fields);
    }
}

public record ReportRefs(IReadOnlySet<string> ModuleKeys, IReadOnlySet<long> FieldIds);
