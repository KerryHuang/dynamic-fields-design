using System.Globalization;
using System.Text.Json;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.Display;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.FieldTypes.Handlers;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ReportService;
public partial class ReportRunner
{
    private static long? LinkIdOf(Record child, Field link) =>
        RecordValueProcessor.Parse(child.ValuesJson).GetValueOrDefault(link.Key) is { } v && ReferenceHandler.KeyOf(v) is { } key
        && long.TryParse(key, CultureInfo.InvariantCulture, out var id) ? id : null;

    private static Dictionary<string, ReportCell> Merge(Dictionary<string, ReportCell> main, Dictionary<string, ReportCell> child)
    {
        var merged = new Dictionary<string, ReportCell>(main);
        foreach (var (k, v) in child) merged[k] = v;
        return merged;
    }

    // ---- 組格子 ----

    /// <summary>每筆記錄、每個被引用的欄位都有一格，缺值是 empty；計算器的分母與條件判斷都靠它。</summary>
    private async Task<Dictionary<long, Dictionary<string, ReportCell>>> BuildCellsAsync(
        Side side, IReadOnlyList<Record> rows, IEnumerable<Bound> all, RunContext ctx, CancellationToken ct)
    {
        var bounds = all.Where(b => b.Source == side).ToList();
        var parsed = rows.Select(r => (Record: r, Values: RecordValueProcessor.Parse(r.ValuesJson))).ToList();
        var refs = bounds.Any(b => b.Via is null && b.Field.Type == FieldType.Reference)
            ? await resolver.ResolveAsync(side.Module, side.Perms!, rows, ct)
            : new Dictionary<long, Dictionary<string, ReferenceInfo>>();

        var viaLookups = new Dictionary<long, ViaLookup>();
        foreach (var g in bounds.Where(b => b.Via is not null).GroupBy(b => b.Via!.Id))
            viaLookups[g.Key] = await LoadViaAsync(g.ToList(), parsed.Select(p => p.Values), ct);

        var result = new Dictionary<long, Dictionary<string, ReportCell>>(rows.Count);
        foreach (var (record, values) in parsed)
        {
            var cells = new Dictionary<string, ReportCell>(bounds.Count);
            foreach (var b in bounds)
            {
                var cell = b.Via is null
                    ? ValueCell(b, b.Field, values.GetValueOrDefault(b.Field.Key), refs.GetValueOrDefault(record.Id)?.GetValueOrDefault(b.Field.Key), ctx)
                    : ViaCell(b, values.GetValueOrDefault(b.Via.Key), viaLookups[b.Via.Id], ctx);
                // 分桶鍵放 Text，Date 留著給區間與 min/max
                if (ctx.Buckets.TryGetValue(b.Key, out var bucket) && cell is { State: Ok, Date: { } date })
                    cell = cell with { Text = ReportDates.BucketKey(date, bucket) };
                cells[b.Key] = cell;
            }
            result[record.Id] = cells;
        }
        return result;
    }

    private sealed record ViaLookup(
        IReadOnlyDictionary<long, (Record Record, Dictionary<string, JsonElement?> Values)> Records,
        IReadOnlyDictionary<long, Dictionary<string, ReferenceInfo>> Refs);

    private static readonly ViaLookup NoVia = new(new Dictionary<long, (Record, Dictionary<string, JsonElement?>)>(), new Dictionary<long, Dictionary<string, ReferenceInfo>>());

    private async Task<ViaLookup> LoadViaAsync(
        IReadOnlyList<Bound> group, IEnumerable<Dictionary<string, JsonElement?>> sourceValues, CancellationToken ct)
    {
        var target = group[0].Target!;
        if (target.Perms is null) return NoVia;
        var via = group[0].Via!;
        var ids = sourceValues.Select(v => v.GetValueOrDefault(via.Key) is { } e && ReferenceHandler.KeyOf(e) is { } k
                && long.TryParse(k, CultureInfo.InvariantCulture, out var id) ? id : (long?)null)
            .Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        var found = await records.ListByIdsAsync(target.Module.Id, ids, ct);
        var refs = group.Any(b => b.Field.Type == FieldType.Reference)
            ? await resolver.ResolveAsync(target.Module, target.Perms, found, ct)
            : new Dictionary<long, Dictionary<string, ReferenceInfo>>();
        return new ViaLookup(found.ToDictionary(r => r.Id, r => (r, RecordValueProcessor.Parse(r.ValuesJson))), refs);
    }

    /// <summary>目標模組不可讀或記錄不在範圍內是 restricted，已刪除是 missing；兩者都不帶目標的值。</summary>
    private static ReportCell ViaCell(Bound b, JsonElement? refValue, ViaLookup lookup, RunContext ctx)
    {
        if (refValue is not { } raw || ReferenceHandler.KeyOf(raw) is not { } key) return EmptyCell;
        if (b.Target!.Perms is not { } perms) return RestrictedCell;
        if (!long.TryParse(key, CultureInfo.InvariantCulture, out var id) || !lookup.Records.TryGetValue(id, out var target)) return MissingCell;
        if (perms.OwnerUserIds is { } owners && !owners.Contains(target.Record.OwnerUserId)) return RestrictedCell;
        return ValueCell(b, b.Field, target.Values.GetValueOrDefault(b.Field.Key),
            lookup.Refs.GetValueOrDefault(id)?.GetValueOrDefault(b.Field.Key), ctx);
    }

    private static ReportCell ValueCell(Bound b, Field f, JsonElement? value, ReferenceInfo? reference, RunContext ctx)
    {
        if (value is not { } v || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return EmptyCell;
        switch (f.Type)
        {
            case FieldType.Integer or FieldType.Decimal or FieldType.Currency or FieldType.Percent:
                return NumberCell(v);
            case FieldType.Formula or FieldType.Rollup:
                return f.Config.ResultType == "date" ? DateCell(v) : NumberCell(v);
            case FieldType.Date:
                return DateCell(v);
            case FieldType.DateTime:
                return v.ValueKind == JsonValueKind.String && TryWallClock(v.GetString()!, out var at) ? new TimedCell(at) : EmptyCell;
            case FieldType.MultiEnum:
                var options = v.EnumerateArray().Select(e => e.GetString()!).ToList();
                return options.Count == 0 ? EmptyCell : new ReportCell(Ok, null, null, null, options);
            case FieldType.Boolean:
                return new ReportCell(Ok, null, v.GetBoolean() ? "true" : "false", null, null);
            case FieldType.Reference:
                if (reference is null) return EmptyCell;
                if (reference.State == ReferenceInfo.Restricted) return RestrictedCell;
                if (reference.State == ReferenceInfo.Missing) return MissingCell;
                ctx.Titles[(b.Key, reference.Key)] = reference.Title ?? reference.Key;
                return new ReportCell(Ok, null, reference.Key, null, null);
            default:
                var text = v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();
                return text.Length == 0 ? EmptyCell : new ReportCell(Ok, null, text, null, null);
        }
    }

    private static ReportCell NumberCell(JsonElement v) =>
        v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n) ? new ReportCell(Ok, n, null, null, null) : EmptyCell;

    private static ReportCell DateCell(JsonElement v) =>
        v.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(v.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? new ReportCell(Ok, null, null, d, null)
            : EmptyCell;

    /// <summary>
    /// 同 BusinessClock.TryParseWallClock：帶偏移的值換成台北牆上時間，不帶偏移的值本身就是台北牆上時間（spec §6.4）。
    /// </summary>
    private static bool TryWallClock(string s, out DateTime at) => BusinessClock.TryParseWallClock(s, out at);
}
