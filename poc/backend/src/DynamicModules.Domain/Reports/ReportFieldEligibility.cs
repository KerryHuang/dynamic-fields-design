using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Formulas;

namespace DynamicModules.Domain.Reports;

/// <summary>報表可用欄位的資格判斷：哪些不能進報表、哪些能分組／加總／取最小最大。</summary>
public static class ReportFieldEligibility
{
    /// <summary>彙總與附件欄位，加上直接或間接引用彙總欄位的公式（彙總值不在記錄上，報表無法取得）。</summary>
    public static IReadOnlySet<long> ExcludedFieldIds(Module m)
    {
        // 附件的值是檔案描述陣列，不是可分組、統計的值，也不進報表
        var excluded = m.Blocks.SelectMany(b => b.Fields).Where(f => f.Type is FieldType.Rollup or FieldType.Attachment).Select(f => f.Id).ToHashSet();
        // EvaluationOrder 被引用者在前，單趟就能傳遞排除
        foreach (var f in FormulaGraph.EvaluationOrder(m))
            if (FormulaGraph.RefsOf(f).Any(excluded.Contains)) excluded.Add(f.Id);
        return excluded;
    }

    public static bool CanGroup(Field f) => f.Type != FieldType.Textarea;

    public static bool CanSum(Field f) =>
        f.Type is FieldType.Integer or FieldType.Decimal or FieldType.Currency or FieldType.Percent
        || (f.Type == FieldType.Formula && f.Config.ResultType is "number" or "currency");

    public static bool CanMinMax(Field f) =>
        CanSum(f) || f.Type is FieldType.Date or FieldType.DateTime
        || (f.Type == FieldType.Formula && f.Config.ResultType == "date");
}
