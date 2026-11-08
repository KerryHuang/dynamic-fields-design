using System.Globalization;
using System.Text.Json;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.ReportService;

/// <summary>
/// 報表的資料庫端聚合（設計：2026-11-10-report-db-aggregation-design）。授權、欄位綁定與隱藏檢查都在 <see cref="RunAsync"/> 前半完成，
/// 這裡只從授權後的 <c>Bound</c> 產生計畫；任何一處不能證明與記憶體路徑逐筆相同，就回傳退回原因，由 <see cref="RunAsync"/> 改走記憶體路徑。
/// </summary>
public partial class ReportRunner
{
    /// <summary>規劃的結果：Query 為 null 時 Reasons 說明為什麼不能走 SQL。</summary>
    private sealed record SqlPlanning(ReportSqlQuery? Query, List<string> Reasons);

    private static decimal Pow10(int n)
    {
        var r = 1m;
        for (var i = 0; i < n; i++) r *= 10m;
        return r;
    }

    /// <summary>欄位型別 → 索引欄種類；null 表示 SQL 路徑不支援這個型別。</summary>
    private static ReportSqlKind? KindOf(Field f) => f.Type switch
    {
        FieldType.Text or FieldType.Phone or FieldType.Email or FieldType.Url or FieldType.Time or FieldType.AutoNumber
            or FieldType.Enum or FieldType.MultiEnum => ReportSqlKind.Text,
        FieldType.User => ReportSqlKind.Ref,
        FieldType.Boolean => ReportSqlKind.Boolean,
        FieldType.Integer or FieldType.Decimal or FieldType.Currency or FieldType.Percent => ReportSqlKind.Number,
        FieldType.Formula => f.Config.ResultType == "date" ? ReportSqlKind.Date : ReportSqlKind.Number,
        FieldType.Date or FieldType.DateTime => ReportSqlKind.Date,
        _ => null,
    };

    /// <summary>文字會被索引截斷的型別（選項、使用者、參照的索引值不經截斷）。</summary>
    private static bool TextMayTruncate(Field f) =>
        f.Type is FieldType.Text or FieldType.Phone or FieldType.Email or FieldType.Url or FieldType.Time or FieldType.AutoNumber;

    private sealed class SqlPlanner(IReadOnlyDictionary<string, Bound> bound, DateOnly basis)
    {
        public readonly List<string> Reasons = [];
        private readonly Dictionary<long, Field> _used = [];
        private readonly HashSet<long> _numberChecks = [], _textChecks = [];

        public IEnumerable<ReportSqlCheck> Checks =>
            _numberChecks.Select(id => new ReportSqlCheck(id, ReportSqlCheckKind.Number))
                .Concat(_textChecks.Select(id => new ReportSqlCheck(id, ReportSqlCheckKind.Text)));

        public IEnumerable<ReportSqlConfigStamp> Stamps => _used.Values.Select(f => new ReportSqlConfigStamp(f.Id, ReportFieldStamp.Of(f)));

        public ReportSqlField? Field(ReportFieldRef r, string? bucket = null, bool checkText = true)
        {
            var b = bound[ReportCalculator.KeyOf(r)];
            if (b.Via is not null) return Fail($"viaNotSupported:{b.Field.Id}");
            var f = b.Field;
            if (!RecordIndexer.ShouldIndex(f)) return Fail($"fieldNotIndexed:{f.Id}");
            if (KindOf(f) is not { } kind) return Fail($"typeNotSupported:{f.Id}");
            _used[f.Id] = f;
            if (kind == ReportSqlKind.Number) _numberChecks.Add(f.Id);
            if (checkText && TextMayTruncate(f)) _textChecks.Add(f.Id);
            return new ReportSqlField(f.Id, kind, ScaledNumber.ScaleOf(f), f.Type == FieldType.MultiEnum, kind == ReportSqlKind.Date ? bucket : null);
        }

        private ReportSqlField? Fail(string reason)
        {
            Reasons.Add(reason);
            return null;
        }

        /// <summary>報表條件 → 計畫條件，語意逐項對應 <c>Matches</c>（設計 §5.4）；不能精確表達就回 null 並記錄原因。</summary>
        public ReportSqlCondition? Condition(ReportFilter f)
        {
            if (Field(f.Field) is not { } field) return null;
            var op = Operators[f.Op];
            if (op == FilterOperator.IsEmpty) return new(field, ReportSqlOp.Empty, []);
            if (op == FilterOperator.IsNotEmpty) return new(field, ReportSqlOp.NotEmpty, []);
            if (op == FilterOperator.BeforeToday) return field.Kind == ReportSqlKind.Date ? DateCondition(field, op, default) : Unsupported(field, f.Op);
            var v = f.Value ?? throw new ArgumentException($"運算子 {f.Op} 需要條件值");
            return field.Kind switch
            {
                ReportSqlKind.Number => NumberCondition(field, op, v, f.Op),
                ReportSqlKind.Date => DateCondition(field, op, v),
                ReportSqlKind.Boolean => BooleanCondition(field, op, v, f.Op),
                _ => TextCondition(field, op, v, f.Op),
            };
        }

        private ReportSqlCondition? Unsupported(ReportSqlField field, string op)
        {
            Reasons.Add($"operatorNotPushable:{op}:{field.FieldId}");
            return null;
        }

        private ReportSqlCondition? TextCondition(ReportSqlField field, FilterOperator op, JsonElement v, string name)
        {
            // 空字串在報表裡是「沒有值」，沒有值的格子不符合 eq／in、符合 ne／notIn，所以條件值裡的空字串可以直接拿掉
            switch (op)
            {
                case FilterOperator.Eq or FilterOperator.Ne:
                    var s = Scalar(v);
                    var negated = op == FilterOperator.Ne;
                    return s.Length == 0 ? new(field, negated ? ReportSqlOp.Always : ReportSqlOp.Never, [])
                        : new(field, ReportSqlOp.Eq, [s], negated);
                case FilterOperator.In or FilterOperator.NotIn:
                    var items = v.EnumerateArray().Select(Scalar).Where(x => x.Length > 0).Distinct().Select(x => (object?)x).ToList();
                    if (items.Count == 0) return new(field, op == FilterOperator.In ? ReportSqlOp.Never : ReportSqlOp.Always, []);
                    return new(field, ReportSqlOp.In, items, Negated: op == FilterOperator.NotIn);
                default: return Unsupported(field, name);
            }
        }

        private ReportSqlCondition? BooleanCondition(ReportSqlField field, FilterOperator op, JsonElement v, string name)
        {
            // 格子的文字是 "true"／"false"，條件值經 Scalar 後比字串；對不上這兩個字串的值永遠不相等
            static long? Bit(string s) => s switch { "true" => 1, "false" => 0, _ => null };
            switch (op)
            {
                case FilterOperator.Eq or FilterOperator.Ne:
                    var negated = op == FilterOperator.Ne;
                    return Bit(Scalar(v)) is { } bit
                        ? new(field, ReportSqlOp.Eq, [bit], negated)
                        : new(field, negated ? ReportSqlOp.Always : ReportSqlOp.Never, []);
                case FilterOperator.In or FilterOperator.NotIn:
                    var bits = v.EnumerateArray().Select(e => Bit(Scalar(e))).Where(b => b is not null).Distinct().Select(b => (object?)b!.Value).ToList();
                    if (bits.Count == 0) return new(field, op == FilterOperator.In ? ReportSqlOp.Never : ReportSqlOp.Always, []);
                    return new(field, ReportSqlOp.In, bits, Negated: op == FilterOperator.NotIn);
                default: return Unsupported(field, name);
            }
        }

        /// <summary>
        /// 數值條件一律用 ScaledValue 精確整數比較（設計 §5.4）：常數在這裡以 decimal 換成 k = c × 10^Scale，
        /// k 不是整數時 eq 恆假、ne 恆真，比較運算換成整數邊界；不經 double。
        /// </summary>
        private ReportSqlCondition? NumberCondition(ReportSqlField field, FilterOperator op, JsonElement v, string name)
        {
            var factor = Pow10(field.Scale);
            try
            {
                switch (op)
                {
                    case FilterOperator.Eq or FilterOperator.Ne:
                        var k = v.GetDecimal() * factor;
                        var negated = op == FilterOperator.Ne;
                        return k == decimal.Truncate(k) ? new(field, ReportSqlOp.Eq, [ToLong(k)], negated)
                            : new(field, negated ? ReportSqlOp.Always : ReportSqlOp.Never, []);
                    case FilterOperator.Gt: return new(field, ReportSqlOp.Gt, [ToLong(decimal.Floor(v.GetDecimal() * factor))]);
                    case FilterOperator.Gte: return new(field, ReportSqlOp.Gte, [ToLong(decimal.Ceiling(v.GetDecimal() * factor))]);
                    case FilterOperator.Lt: return new(field, ReportSqlOp.Lt, [ToLong(decimal.Ceiling(v.GetDecimal() * factor))]);
                    case FilterOperator.Lte: return new(field, ReportSqlOp.Lte, [ToLong(decimal.Floor(v.GetDecimal() * factor))]);
                    case FilterOperator.Between:
                        return new(field, ReportSqlOp.Between,
                            [ToLong(decimal.Ceiling(v[0].GetDecimal() * factor)), ToLong(decimal.Floor(v[1].GetDecimal() * factor))]);
                    case FilterOperator.In or FilterOperator.NotIn:
                        var members = v.EnumerateArray().Select(e => e.GetDecimal() * factor).Where(x => x == decimal.Truncate(x))
                            .Select(x => (object?)ToLong(x)).Distinct().ToList();
                        if (members.Count == 0) return new(field, op == FilterOperator.In ? ReportSqlOp.Never : ReportSqlOp.Always, []);
                        return new(field, ReportSqlOp.In, members, Negated: op == FilterOperator.NotIn);
                    default: return Unsupported(field, name);
                }
            }
            catch (Exception e) when (e is OverflowException or ConstantOutOfRange)
            {
                Reasons.Add($"conditionConstantOutOfRange:{field.FieldId}");
                return null;
            }
        }

        private sealed class ConstantOutOfRange : Exception;

        private static long ToLong(decimal d) =>
            d is >= long.MinValue and <= long.MaxValue ? (long)d : throw new ConstantOutOfRange();

        private ReportSqlCondition? DateCondition(ReportSqlField field, FilterOperator op, JsonElement v)
        {
            static ReportSqlCondition Range(ReportSqlField f, DateTime? lo, DateTime? hi, bool negated = false) =>
                new(f, ReportSqlOp.Range, [lo, hi], negated);
            var midnight = basis.ToDateTime(TimeOnly.MinValue);
            switch (op)
            {
                case FilterOperator.WithinNextDays: return Range(field, midnight, basis.AddDays(v.GetInt32() + 1).ToDateTime(TimeOnly.MinValue));
                case FilterOperator.WithinLastDays: return Range(field, basis.AddDays(-v.GetInt32()).ToDateTime(TimeOnly.MinValue), basis.AddDays(1).ToDateTime(TimeOnly.MinValue));
                case FilterOperator.BeforeToday: return Range(field, null, midnight);
                case FilterOperator.Between: return Range(field, Span(v[0]).From, Span(v[1]).Until);
            }
            var (from, until) = Span(v);
            return op switch
            {
                FilterOperator.Eq => Range(field, from, until),
                FilterOperator.Ne => Range(field, from, until, negated: true),
                FilterOperator.Gt => Range(field, until, null),
                FilterOperator.Gte => Range(field, from, null),
                FilterOperator.Lt => Range(field, null, from),
                FilterOperator.Lte => Range(field, null, until),
                _ => Unsupported(field, op.ToString()),
            };
        }
    }

    // ---- 計畫：整張聚合報表 ----

    /// <summary>
    /// 把授權後的報表定義規劃成 SQL 聚合；不能走 SQL 時 Query 為 null、Reasons 為退回原因。
    /// 只處理「沒有子模組、不下鑽、不是明細」的聚合報表（子模組合併與參照欄位的 SQL 路徑尚未支援，走縮小後的記憶體路徑）。
    /// </summary>
    private SqlPlanning PlanAggregate(
        ReportDefinition d, ReportRunOptions o, IReadOnlyDictionary<string, Bound> bound, Side mainSide, DateOnly basis,
        (DateOnly From, DateOnly ToInclusive)? window)
    {
        var planner = new SqlPlanner(bound, basis);
        var reasons = planner.Reasons;
        if (d.Child is not null) reasons.Add("mergedNotSupported");
        if (o.DrillGroupKeys is not null) reasons.Add("drill");
        if (FieldColumnsFor(d, drilling: false).Any()) reasons.Add("detail");
        if (d.Groups.Count == 0 && d.Measures.Count == 0) reasons.Add("noAggregate");
        if (reasons.Count > 0) return new(null, reasons);

        var groups = new List<ReportSqlField>();
        foreach (var g in d.Groups)
            if (planner.Field(g.Field, g.Bucket) is { } sf) groups.Add(sf);

        var measures = new List<ReportSqlMeasure>();
        foreach (var m in d.Measures)
        {
            ReportSqlField? mf = null;
            ReportSqlCondition? cond = null;
            if (m.Kind == "ratio")
                cond = planner.Condition(m.Condition!);
            else if (m.Field is not null)
                mf = planner.Field(m.Field, checkText: false);
            if ((m.Kind == "ratio" && cond is null) || (m.Field is not null && m.Kind != "ratio" && mf is null)) continue;
            measures.Add(new ReportSqlMeasure(m.Id, m.Kind, mf, cond));
        }

        var filters = new List<ReportSqlCondition>();
        foreach (var f in d.Filters)
            if (planner.Condition(f) is { } c) filters.Add(c);
        if (window is { } w && d.PrimaryDateField is not null && planner.Field(d.PrimaryDateField, checkText: false) is { } df)
            filters.Add(new(df, ReportSqlOp.Range, [w.From.ToDateTime(TimeOnly.MinValue), w.ToInclusive.AddDays(1).ToDateTime(TimeOnly.MinValue)]));

        if (reasons.Count > 0) return new(null, reasons);
        return new(new ReportSqlQuery(new ReportSqlScope(mainSide.Module.Id, mainSide.Perms!.OwnerUserIds), groups, measures, filters,
            [.. planner.Checks], [.. planner.Stamps], options.MaxRows), reasons);
    }

    // ---- 縮小後備載入的條件（設計 §8.1）----

    /// <summary>
    /// 主模組上可精確表達的條件：篩選、主模組側的主要日期區間，沒有子模組時再加下鑽的分組鍵。
    /// 表達不了的條件直接略過（不縮小，只會多載入）。沒有任何條件時回 null。
    /// </summary>
    private ReportSqlNarrowing? NarrowingFor(
        ReportDefinition d, ReportRunOptions o, IReadOnlyDictionary<string, Bound> bound, DateOnly basis,
        (DateOnly From, DateOnly ToInclusive)? window)
    {
        var planner = new SqlPlanner(bound, basis);
        var conditions = new List<ReportSqlCondition>();
        foreach (var f in d.Filters.Where(f => f.Field.Source == "main"))
            if (planner.Condition(f) is { } c) conditions.Add(c);
        if (window is { } w && d.PrimaryDateField is { Source: "main" } primary && planner.Field(primary, checkText: false) is { } df)
            conditions.Add(new(df, ReportSqlOp.Range, [w.From.ToDateTime(TimeOnly.MinValue), w.ToInclusive.AddDays(1).ToDateTime(TimeOnly.MinValue)]));
        if (d.Child is null && o.DrillGroupKeys is { } keys && keys.Count == d.Groups.Count)
            for (var i = 0; i < keys.Count; i++)
                if (DrillCondition(planner, d.Groups[i], keys[i]) is { } dc) conditions.Add(dc);
        return conditions.Count == 0 ? null : new ReportSqlNarrowing(conditions, [.. planner.Checks], [.. planner.Stamps]);
    }

    /// <summary>下鑽分組鍵 → 縮小條件，只涵蓋鍵與索引值一對一的情形；分桶、無法對應的鍵不縮小。</summary>
    private static ReportSqlCondition? DrillCondition(SqlPlanner planner, ReportGroup g, string key)
    {
        if (g.Bucket is not null) return null;
        if (planner.Field(g.Field) is not { } field) return null;
        switch (field.Kind)
        {
            case ReportSqlKind.Text or ReportSqlKind.Ref:
                return key.Length == 0 ? new(field, ReportSqlOp.Empty, []) : new(field, ReportSqlOp.Eq, [key]);
            case ReportSqlKind.Boolean:
                return key switch
                {
                    "" => new(field, ReportSqlOp.Empty, []),
                    "true" => new(field, ReportSqlOp.Eq, [1L]),
                    "false" => new(field, ReportSqlOp.Eq, [0L]),
                    _ => null,
                };
            case ReportSqlKind.Number:
                if (key.Length == 0) return new(field, ReportSqlOp.Empty, []);
                if (!decimal.TryParse(key, NumberStyles.Number, CultureInfo.InvariantCulture, out var n)) return null;
                var scaled = n * Pow10(field.Scale);
                return scaled == decimal.Truncate(scaled) && scaled is >= long.MinValue and <= long.MaxValue
                    ? new(field, ReportSqlOp.Eq, [(long)scaled]) : null;
            case ReportSqlKind.Date:
                if (key.Length == 0) return new(field, ReportSqlOp.Empty, []);
                return DateOnly.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                    ? new(field, ReportSqlOp.Range, [day.ToDateTime(TimeOnly.MinValue), day.AddDays(1).ToDateTime(TimeOnly.MinValue)])
                    : null;
            default: return null;
        }
    }

    /// <summary>SQL 聚合的入口：回 null 表示不能走 SQL（原因已加進 <paramref name="reasons"/>），由呼叫端改走記憶體路徑。</summary>
    private async Task<ReportResult?> TrySqlAsync(
        ReportDefinition d, ReportRunOptions o, IReadOnlyDictionary<string, Bound> bound, Side mainSide, Side? childSide, RunContext ctx,
        DateOnly basis, (DateOnly, DateOnly)? resolved, bool frozen, ReportAppliedRange? applied, List<string> reasons, CancellationToken ct)
    {
        var planning = PlanAggregate(d, o, bound, mainSide, basis, resolved);
        if (planning.Query is not { } query)
        {
            reasons.AddRange(planning.Reasons);
            return null;
        }
        // 升級前的資料要等一次性回填完成才確定 ScaledValue 與截斷標記可用（設計 §8.3）
        if (await settings.GetValueAsync(SystemSetting.IndexBackfillVersion, ct) != RecordIndexer.BackfillVersion)
        {
            reasons.Add("indexBackfillPending");
            return null;
        }
        var outcome = await queries.AggregateAsync(query, ct);
        if (outcome.FallbackReason == ReportSqlOutcome.TimeoutReason) return SqlTimeoutResult(ctx.Locale, basis, frozen);
        if (outcome.FallbackReason is { } reason)
        {
            reasons.Add(reason);
            return null;
        }
        // 分組數上限沿用記憶體路徑的 MaxRows，訊息相同（近乎唯一的欄位分組會產生與記錄數同量的分組）
        if (outcome.Groups.Count > options.MaxRows)
            return new ReportResult(TooManyState, ReportText.Get(ctx.Locale, ReportText.TooMany), "", [], null, null, null, false, basis, null, frozen,
                Engine: "sql", FallbackReasons: []);
        return BuildSqlResult(d, query, outcome, bound, mainSide, childSide, ctx, basis, frozen, applied);
    }

    /// <summary>縮小後備載入：回 null 表示不縮小（沒有可用的精確條件、回填未完成或探測沒過），呼叫端載入整個模組。</summary>
    private async Task<ReportNarrowedRecords?> TryNarrowAsync(
        ReportDefinition d, ReportRunOptions o, IReadOnlyDictionary<string, Bound> bound, Side mainSide, Side? childSide, Field? link,
        DateOnly basis, (DateOnly, DateOnly)? resolved, List<string> reasons, CancellationToken ct)
    {
        if (await settings.GetValueAsync(SystemSetting.IndexBackfillVersion, ct) != RecordIndexer.BackfillVersion) return null;
        if (NarrowingFor(d, o, bound, basis, resolved) is not { } narrowing) return null;
        var result = await queries.ListNarrowedAsync(
            new ReportSqlScope(mainSide.Module.Id, mainSide.Perms!.OwnerUserIds), narrowing,
            childSide is null ? null : new ReportSqlScope(childSide.Module.Id, childSide.Perms!.OwnerUserIds), link?.Id ?? 0,
            options.MaxLoadRows + 1, ct);
        // 逾時照原樣交回，呼叫端回逾時錯誤；改成載入整個模組只會把負載放大
        if (result.FallbackReason == ReportSqlOutcome.TimeoutReason) return result;
        if (result.FallbackReason is { } reason)
        {
            reasons.Add($"narrowSkipped:{reason}");
            return null;
        }
        return result;
    }

    private static ReportResult SqlTimeoutResult(string locale, DateOnly basis, bool frozen) =>
        new(TooManyState, ReportText.Get(locale, ReportText.SqlTimeout), "", [], null, null, null, false, basis, null, frozen,
            Engine: "sql", FallbackReasons: []);

    // ---- 結果：SQL 的原始列 → 與記憶體路徑相同的計算結果 ----

    private static decimal Normalize(decimal d) => d / 1.0000000000000000000000000000m;

    private static string NumberKey(long? scaled, int scale) =>
        scaled is null ? "" : ((decimal)scaled.Value / Pow10(scale)).ToString("0.############################", CultureInfo.InvariantCulture);

    private static Dictionary<string, decimal?> MeasureValues(ReportSqlRow row, IReadOnlyList<ReportSqlMeasure> measures)
    {
        var values = new Dictionary<string, decimal?>();
        for (var j = 0; j < measures.Count; j++)
        {
            var m = measures[j];
            var raw = row.Measures[j];
            decimal? value = m.Kind switch
            {
                "countMain" => row.RowCount,
                "countChild" => 0,
                "ratio" => row.RowCount == 0 ? null : (decimal)raw.Hit / row.RowCount,
                "sum" => raw.Count == 0 ? null : Normalize(Sum(raw) / Pow10(m.Field!.Scale)),
                "avg" => raw.Count == 0 ? null : Normalize(Sum(raw) / Pow10(m.Field!.Scale) / raw.Count),
                _ => m.Field!.Kind == ReportSqlKind.Date
                    ? (m.Kind == "min" ? raw.MinDate : raw.MaxDate) is { } dt ? DateOnly.FromDateTime(dt).DayNumber : null
                    : (m.Kind == "min" ? raw.MinScaled : raw.MaxScaled) is { } s ? Normalize((decimal)s / Pow10(m.Field.Scale)) : null,
            };
            values[m.Id] = value;
        }
        return values;
    }

    private static decimal Sum(ReportSqlMeasureRaw raw) => raw.SumHi * 1_000_000_000m + raw.SumLo;

    /// <summary>SQL 路徑成功時組出最終結果：分組鍵與量值換成 <see cref="ReportCalcResult"/>，其餘（標籤、排序、欄位）走與記憶體路徑相同的程式。</summary>
    private ReportResult BuildSqlResult(
        ReportDefinition d, ReportSqlQuery query, ReportSqlOutcome outcome, IReadOnlyDictionary<string, Bound> bound,
        Side mainSide, Side? childSide, RunContext ctx, DateOnly basis, bool frozen, ReportAppliedRange? applied)
    {
        var locale = ctx.Locale;
        var groups = outcome.Groups.Select(row =>
        {
            var keys = new List<string>(query.Groups.Count);
            for (var i = 0; i < query.Groups.Count; i++)
                keys.Add(row.Keys[i] switch
                {
                    long scaled => NumberKey(scaled, query.Groups[i].Scale),
                    string s => s,
                    _ => "",
                });
            return new ReportGroupResult(keys, keys, MeasureValues(row, query.Measures));
        }).ToList();
        var total = outcome.Total!;
        var multiGroup = d.Groups.Any(g => bound[ReportCalculator.KeyOf(g.Field)].Field.Type == FieldType.MultiEnum);
        var calc = new ReportCalcResult(groups, MeasureValues(total, query.Measures),
            ReportCalculator.NeedsDedupedNote(d.Measures, d.Child is not null, multiGroup), (int)total.RowCount);

        var columns = new List<ReportColumn>();
        columns.AddRange(d.Groups.Select(g =>
        {
            var b = bound[ReportCalculator.KeyOf(g.Field)];
            return new ReportColumn(b.Key, ColumnLabel(b, locale), "group", g.Bucket is null ? ValueTypeOf(b.Field) : "text");
        }));
        columns.AddRange(d.Measures.Select(m => MeasureColumn(m, bound, mainSide, childSide, locale)));
        var labeled = LabeledGroups(calc.Groups, d, bound, ctx);
        return new ReportResult(calc.RowCount == 0 ? Empty : Ok, null, ReportText.Get(locale, ReportText.RowMeaning, mainSide.Module.NameI18n.Resolve(locale)),
            columns, labeled, calc.Total, null, calc.DedupedNote, basis, applied, frozen, null, false, null, null, false, "sql", []);
    }
}
