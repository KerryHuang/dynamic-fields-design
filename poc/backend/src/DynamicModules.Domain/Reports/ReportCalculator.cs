using System.Globalization;

namespace DynamicModules.Domain.Reports;

/// <summary>報表的分組、彙總與去重：純函式，不碰資料庫（spec §6.3）。</summary>
public static class ReportCalculator
{
    public const string MissingKey = "__missing";
    public const string RestrictedKey = "__restricted";

    public static string KeyOf(ReportFieldRef f) => $"{f.Source}:{f.FieldId}:{f.ViaFieldId?.ToString(CultureInfo.InvariantCulture)}";

    /// <param name="merged">報表是否合併了子模組；只影響 DedupedNote。</param>
    /// <param name="multiGroup">定義是否依多選欄位分組；只影響 DedupedNote。下鑽時 groups 是空的，所以要另外傳。</param>
    /// <param name="matches">ratio 量值判斷一格是否符合條件；有 ratio 量值時必須提供，運算子由欄位型別層實作。</param>
    /// <remarks>
    /// 分組依第一次出現的順序，排序由呼叫端做。沒有分組時 Groups 為空，數字都在 Total。
    /// 日期分組的桶（週、月…）由呼叫端先寫進 cell.Text。主模組欄位的量值在每個分組內以 MainId 去重，子模組欄位逐列算。
    /// sum、avg、min、max 沒有任何值時是 null；countMain、countChild 是 0；ratio 分母為 0 時是 null。
    /// </remarks>
    public static ReportCalcResult Calculate(
        IReadOnlyList<ReportRow> rows, IReadOnlyList<ReportGroup> groups, IReadOnlyList<ReportMeasure> measures, bool merged,
        Func<ReportFilter, ReportCell, bool>? matches = null, bool multiGroup = false)
    {
        if (matches is null && measures.Any(m => m.Kind == "ratio"))
            throw new ArgumentException("ratio 量值需要提供條件判斷函式 matches。", nameof(matches));

        var buckets = new List<(List<string> Keys, List<ReportRow> Rows)>();
        if (groups.Count > 0)
        {
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var row in rows)
                foreach (var keys in GroupKeysOf(row, groups))
                {
                    var id = string.Join('\u001f', keys);
                    if (!index.TryGetValue(id, out var i))
                    {
                        i = buckets.Count;
                        index[id] = i;
                        buckets.Add((keys, []));
                    }
                    buckets[i].Rows.Add(row);
                }
        }

        var results = buckets.Select(b => new ReportGroupResult(b.Keys, b.Keys, Measure(b.Rows, measures, matches))).ToList();
        return new ReportCalcResult(results, Measure(rows, measures, matches), NeedsDedupedNote(measures, merged, multiGroup), rows.Count);
    }

    /// <summary>
    /// 列相加或下鑽列數對不上總計時要說明（spec §6.3、§13.1）。合併時主模組的數字每筆只算一次；
    /// 依多選欄位分組時一筆落在多組，任何相加型量值都會加超過總計。最小、最大與條件占比不是相加出來的，不需要說明。
    /// </summary>
    private static bool NeedsDedupedNote(IReadOnlyList<ReportMeasure> measures, bool merged, bool multiGroup) =>
        (merged && measures.Any(m => m.Kind == "countMain" || (m.Kind is "sum" or "avg" && m.Field?.Source == "main")))
        || (multiGroup && measures.Any(m => m.Kind is "sum" or "avg" or "countMain" or "countChild"));

    /// <summary>一列在各分組欄位的 key 組合；多選每個選項一組，所以一列可能落在多個分組。</summary>
    private static IEnumerable<List<string>> GroupKeysOf(ReportRow row, IReadOnlyList<ReportGroup> groups)
    {
        IEnumerable<List<string>> combos = [[]];
        foreach (var g in groups)
        {
            var keys = KeysOf(row.Cells.GetValueOrDefault(KeyOf(g.Field)));
            combos = combos.SelectMany(c => keys.Select(k => new List<string>(c) { k })).ToList();
        }
        return combos;
    }

    /// <summary>一格落在哪些分組 key；下鑽用它判斷一列是否屬於某分組。</summary>
    public static List<string> KeysOf(ReportCell? cell)
    {
        if (cell is null) return [""];
        switch (cell.State)
        {
            case "missing": return [MissingKey];
            case "restricted": return [RestrictedKey];
            case "ok":
                if (cell.Multi is { Count: > 0 }) return cell.Multi.Distinct().ToList();
                if (cell.Text is not null) return [cell.Text];
                if (cell.Number is not null) return [cell.Number.Value.ToString("0.############################", CultureInfo.InvariantCulture)];
                if (cell.Date is not null) return [cell.Date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)];
                return [""];
            default: return [""];
        }
    }

    private static Dictionary<string, decimal?> Measure(
        IReadOnlyList<ReportRow> rows, IReadOnlyList<ReportMeasure> measures, Func<ReportFilter, ReportCell, bool>? matches)
    {
        var values = new Dictionary<string, decimal?>();
        foreach (var m in measures) values[m.Id] = MeasureOne(rows, m, matches);
        return values;
    }

    private static decimal? MeasureOne(
        IReadOnlyList<ReportRow> rows, ReportMeasure m, Func<ReportFilter, ReportCell, bool>? matches)
    {
        switch (m.Kind)
        {
            case "countMain": return rows.Select(r => r.MainId).Distinct().Count();
            case "countChild": return rows.Count(r => r.ChildId is not null);
            case "ratio":
                var cond = m.Condition ?? throw new ArgumentException($"ratio 量值 {m.Id} 沒有條件。");
                var scope = Scope(rows, cond.Field.Source);
                if (scope.Count == 0) return null;
                var hit = scope.Count(r => r.Cells.TryGetValue(KeyOf(cond.Field), out var c) && matches!(cond, c));
                return (decimal)hit / scope.Count;
        }

        var field = m.Field ?? throw new ArgumentException($"量值 {m.Id} 沒有欄位。");
        var cells = Scope(rows, field.Source)
            .Select(r => r.Cells.GetValueOrDefault(KeyOf(field)))
            .Where(c => c is { State: "ok" })
            .ToList();
        var numbers = cells.Where(c => c!.Number is not null).Select(c => c!.Number!.Value).ToList();
        var days = cells.Where(c => c!.Date is not null).Select(c => (decimal)c!.Date!.Value.DayNumber).ToList();
        var picked = numbers.Count > 0 ? numbers : days;
        if (picked.Count == 0) return null;
        return m.Kind switch
        {
            "sum" => picked.Sum(),
            "avg" => picked.Sum() / picked.Count,
            "min" => picked.Min(),
            "max" => picked.Max(),
            _ => throw new ArgumentException($"不支援的量值種類 {m.Kind}。")
        };
    }

    /// <summary>主模組欄位每個 MainId 只留第一列（同一主記錄各列的主欄位值相同）；子模組欄位只算有子記錄的列。</summary>
    private static List<ReportRow> Scope(IReadOnlyList<ReportRow> rows, string source) =>
        source == "main"
            ? rows.GroupBy(r => r.MainId).Select(g => g.First()).ToList()
            : rows.Where(r => r.ChildId is not null).ToList();
}
