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

/// <summary>
/// 把報表定義跑成結果：授權、載入記錄、組列、交給 <see cref="ReportCalculator"/>。數字一律照目前使用者的權限計算（spec §7）。
/// 帶出欄位以 Via 指向來源模組的參照欄位、FieldId 指向目標模組的欄位；既有實體目標只有標題可帶，FieldId 固定為 <see cref="EntityTitleFieldId"/>。
/// </summary>
public class ReportRunner(
    IModuleRepository modules, IRecordRepository records, IPermissionRepository permissions, IDynamicAccessGuard guard,
    ReferenceResolver resolver, FieldTypeRegistry registry, IBusinessTime time, ReportOptions options,
    AuthSettings auth, ISystemSettingRepository settings)
{
    public const long EntityTitleFieldId = 0;

    private const string Ok = "ok", Empty = "empty", Missing = "missing", Restricted = "restricted";
    private const string FieldMissingState = "fieldMissing", ModuleDisabledState = "moduleDisabled", TooManyState = "tooMany";
    private const string HiddenFieldName = "隱藏欄位";
    private const string DeletedLabel = "（已刪除）", RestrictedLabel = "（無權限）", BlankLabel = "（空白）";

    private static readonly ReportCell EmptyCell = new(Empty, null, null, null, null);
    private static readonly ReportCell MissingCell = new(Missing, null, null, null, null);
    private static readonly ReportCell RestrictedCell = new(Restricted, null, null, null, null);

    private static readonly Dictionary<string, FilterOperator> Operators =
        Enum.GetValues<FilterOperator>().ToDictionary(o => JsonNamingPolicy.CamelCase.ConvertName(o.ToString()));

    /// <summary>日期時間欄位的格子：Date 是台北牆上日期，At 留給條件比對到時分。</summary>
    private sealed record TimedCell(DateTime At) : ReportCell(Ok, null, null, DateOnly.FromDateTime(At), null);

    /// <summary>一個模組與目前使用者對它的權限；Perms 為 null 表示參照目標模組不可讀。</summary>
    private sealed record Side(Module Module, EffectivePermissions? Perms)
    {
        public IReadOnlyDictionary<long, Field> Fields { get; } = Module.Blocks.SelectMany(b => b.Fields).ToDictionary(f => f.Id);
        public IReadOnlySet<long> Excluded { get; } = ReportFieldEligibility.ExcludedFieldIds(Module);
        public Field? Active(long? id) => id is { } i && Fields.TryGetValue(i, out var f) && f.Status == FieldStatus.Active ? f : null;
    }

    /// <summary>定義裡的欄位引用解析後的結果。Via、Target 只有帶出動態模組的欄位才有值；實體目標當成來源模組的參照欄位，Field 就是該參照欄位。</summary>
    private sealed record Bound(ReportFieldRef Ref, string Key, Side Source, Field Field, Field? Via, Side? Target)
    {
        public bool MayBeUnavailable => Via is not null || Field.Type == FieldType.Reference;
    }

    /// <summary>一次執行的上下文：語系、使用者姓名、參照標題（key 是欄位鍵與參照鍵）。</summary>
    private sealed record RunContext(
        string Locale, IReadOnlyDictionary<string, string> UserNames, Dictionary<(string, string), string> Titles,
        IReadOnlyDictionary<string, string> Buckets);

    /// <summary>報表的基準日：Demo 登入開啟且產生器留有產生日期時用它（Demo 資料日期停在建置那天），否則是今天。</summary>
    public async Task<DateOnly> BasisDateAsync(CancellationToken ct)
    {
        if (auth.DemoLoginEnabled
            && await settings.GetValueAsync(SystemSetting.DemoGeneratedOn, ct) is { } value
            && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var generatedOn))
            return generatedOn;
        return time.Today;
    }

    public async Task<ReportResult> RunAsync(ReportDefinition d, ReportRunOptions o, CancellationToken ct)
    {
        var basis = await BasisDateAsync(ct);
        var frozen = basis != time.Today;
        var locale = string.IsNullOrEmpty(o.Locale) ? I18nText.FallbackLocale : o.Locale;
        ReportResult Fail(string state, string message) => new(state, message, "", [], null, null, null, false, basis, null, frozen);
        var disabled = () => Fail(ModuleDisabledState, "模組已停用");

        // 1. 模組存在且啟用；明細模組不能當主模組（它的權限來自表頭，單獨跑會繞過表頭範圍）
        var main = await ActiveModuleAsync(d.MainModuleKey, ct);
        if (main is null) return disabled();
        if (ModuleRelations.IsDetail(main)) return Fail(ModuleDisabledState, "明細模組不能當主模組");
        Module? child = null;
        if (d.Child is not null && (child = await ActiveModuleAsync(d.Child.ModuleKey, ct)) is null) return disabled();
        Module? childParent = null;
        if (child is not null && ModuleRelations.ParentKeyOf(child) is { } parentKey)
        {
            childParent = parentKey == main.Key ? main : await ActiveModuleAsync(parentKey, ct);
            if (childParent is null) return disabled();
        }

        var refs = AllRefs(d);
        var targets = new Dictionary<string, Module>();
        foreach (var r in refs.Where(r => r.ViaFieldId is not null))
        {
            var source = r.Source == "main" ? main : r.Source == "child" ? child : null;
            var via = source?.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == r.ViaFieldId && f.Status == FieldStatus.Active);
            if (via?.Config.RefTarget is not { Kind: ReferenceTarget.ModuleKind } target || targets.ContainsKey(target.Key)) continue;
            var module = await ActiveModuleAsync(target.Key, ct);
            if (module is null) return disabled();
            targets[target.Key] = module;
        }

        // 2. 模組 Read
        var mainAccess = await guard.AuthorizeAsync(main, PermissionAction.Read, ct);
        if (mainAccess.Permissions is null) return Fail(Restricted, "無權限檢視這個模組");
        var mainSide = new Side(main, mainAccess.Permissions);
        Side? childSide = null;
        if (child is not null)
        {
            var access = childParent is not null
                ? await guard.AuthorizeDetailAsync(childParent, child, PermissionAction.Read, ct)
                : await guard.AuthorizeAsync(child, PermissionAction.Read, ct);
            if (access.Permissions is null) return Fail(Restricted, "無權限檢視合併的子模組");
            childSide = new Side(child, access.Permissions);
        }
        var targetSides = new Dictionary<string, Side>();
        foreach (var (key, module) in targets)
            targetSides[key] = new Side(module, (await guard.AuthorizeAsync(module, PermissionAction.Read, ct)).Permissions);

        // 3. 欄位存在且啟用
        var link = d.Child is null ? null : childSide!.Active(d.Child.LinkFieldId);
        if (d.Child is not null && link is not { Type: FieldType.Reference, Config.RefTarget: { Kind: ReferenceTarget.ModuleKind } } || (link is not null && link.Config.RefTarget!.Key != main.Key))
            return Fail(FieldMissingState, MissingMessage(childSide!, d.Child!.LinkFieldId, locale));

        var bound = new Dictionary<string, Bound>();
        foreach (var r in refs)
        {
            var key = ReportCalculator.KeyOf(r);
            if (bound.ContainsKey(key)) continue;
            var (b, problem) = Bind(r, key, mainSide, childSide, targetSides, locale);
            if (b is null) return Fail(FieldMissingState, problem!);
            bound[key] = b;
        }

        // 4. 看不到或不能用的欄位：任何位置都整張擋下，訊息只寫看得到的名稱
        if (link is not null && !childSide!.Perms!.CanSee(link)) return Fail(Restricted, $"無權限檢視：{HiddenFieldName}");
        foreach (var b in bound.Values)
            if (UnusableMessage(b, locale) is { } message) return Fail(Restricted, message);

        // 條件與量值用到不可讀模組的帶出欄位，結果會是假的 empty、0% 或 null，整張擋下（spec §7、§13.2）；
        // 欄位與分組位置照常，值標無權限。範圍內單筆看不到的目標仍是逐列不符合，見 Matches。
        var conditionRefs = d.Filters.Select(f => f.Field).Concat(d.Measures.Where(m => m.Condition is not null).Select(m => m.Condition!.Field)).ToList();
        var computedRefs = conditionRefs.Concat(d.Measures.Where(m => m.Field is not null).Select(m => m.Field!));
        if (d.PrimaryDateField is not null) computedRefs = computedRefs.Append(d.PrimaryDateField);
        if (computedRefs.Any(r => bound[ReportCalculator.KeyOf(r)].Target is { Perms: null }))
            return Fail(Restricted, $"無權限檢視：{HiddenFieldName}");

        // 參照欄位本身放在條件裡同理：目標整個看不到時每一格都是 restricted，條件永遠不符合
        foreach (var f in conditionRefs.Select(r => bound[ReportCalculator.KeyOf(r)].Field).Where(f => f.Type == FieldType.Reference).DistinctBy(f => f.Id))
        {
            if (f.Config.RefTarget is not { Kind: ReferenceTarget.ModuleKind } refTarget) continue;
            var target = await ActiveModuleAsync(refTarget.Key, ct);
            if (target is null) return disabled();
            if ((await guard.AuthorizeAsync(target, PermissionAction.Read, ct)).Permissions is not { } perms || !target.TitleVisibleTo(perms))
                return Fail(Restricted, $"無權限檢視：{HiddenFieldName}");
        }

        // 權限都過了才檢查運算子，錯誤不會洩漏欄位資訊
        foreach (var f in d.Filters.Concat(d.Measures.Where(m => m.Condition is not null).Select(m => m.Condition!)))
            EnsureOperator(f, bound[ReportCalculator.KeyOf(f.Field)]);

        // 5. 載入：多取一筆才分得出是否超過上限；超過就不回部分結果
        var tooMany = () => Fail(TooManyState, "資料太多，請縮小條件");
        var mainRows = await records.ListForReportAsync(main.Id, mainSide.Perms!.OwnerUserIds, options.MaxRows + 1, ct);
        if (mainRows.Count > options.MaxRows) return tooMany();
        IReadOnlyList<Record> childRows = [];
        if (childSide is not null)
        {
            childRows = await records.ListForReportAsync(childSide.Module.Id, childSide.Perms!.OwnerUserIds, options.MaxRows + 1, ct);
            if (childRows.Count > options.MaxRows) return tooMany();
        }

        var userNames = bound.Values.Any(b => b.Field.Type == FieldType.User)
            ? (await permissions.ListUsersAsync(ct)).ToDictionary(u => u.UserId, u => u.DisplayName)
            : new Dictionary<string, string>();
        var buckets = d.Groups.Where(g => g.Bucket is not null).ToDictionary(g => ReportCalculator.KeyOf(g.Field), g => g.Bucket!);
        var ctx = new RunContext(locale, userNames, [], buckets);

        var mainCells = await BuildCellsAsync(mainSide, mainRows, bound.Values, ctx, ct);
        var childCells = childSide is null
            ? new Dictionary<long, Dictionary<string, ReportCell>>()
            : await BuildCellsAsync(childSide, childRows, bound.Values, ctx, ct);

        // 6. 條件先套主模組與其帶出欄位，再過濾子記錄（spec §6.2）；日期區間作用在主要日期欄位那一邊
        var range = (o.RangeOverride ?? d.DateRange) is { } rangeDef && d.PrimaryDateField is not null ? rangeDef : null;
        var resolved = range is null ? ((DateOnly, DateOnly)?)null : ReportDates.Resolve(range, basis);
        bool Passes(string source, IReadOnlyDictionary<string, ReportCell> cells) =>
            d.Filters.Where(f => f.Field.Source == source).All(f => Matches(f, cells[ReportCalculator.KeyOf(f.Field)], basis))
            && (resolved is not { } window || d.PrimaryDateField!.Source != source
                || ReportDates.Contains(window, cells[ReportCalculator.KeyOf(d.PrimaryDateField)]));

        var rows = new List<ReportRow>();
        var childConditioned = d.Filters.Any(f => f.Field.Source == "child") || (resolved is not null && d.PrimaryDateField!.Source == "child");
        var kidsByMain = childRows.Where(r => Passes("child", childCells[r.Id]))
            .ToLookup(r => LinkIdOf(r, link!));
        var blankChild = bound.Values.Where(b => b.Source == childSide).ToDictionary(b => b.Key, _ => EmptyCell);
        foreach (var m in mainRows)
        {
            var cells = mainCells[m.Id];
            if (!Passes("main", cells)) continue;
            if (childSide is null)
            {
                rows.Add(new ReportRow(m.Id, null, cells));
                continue;
            }
            var kids = kidsByMain[m.Id].ToList();
            if (kids.Count == 0 && !childConditioned) rows.Add(new ReportRow(m.Id, null, Merge(cells, blankChild)));
            foreach (var kid in kids) rows.Add(new ReportRow(m.Id, kid.Id, Merge(cells, childCells[kid.Id])));
        }
        if (rows.Count > options.MaxRows) return tooMany();

        // 7. 下鑽只留該分組的列；各欄位值的 key 判斷與分組相同
        var drilling = o.DrillGroupKeys is not null;
        if (o.DrillGroupKeys is { } drill)
            rows = drill.Count != d.Groups.Count
                ? []
                : rows.Where(r => d.Groups.Select((g, i) => ReportCalculator.KeysOf(r.Cells[ReportCalculator.KeyOf(g.Field)]).Contains(drill[i])).All(x => x)).ToList();

        var calc = ReportCalculator.Calculate(rows, drilling ? [] : d.Groups, d.Measures, d.Child is not null, (f, c) => Matches(f, c, basis));

        var rowModule = childSide?.Module ?? main;
        var fieldColumns = FieldColumnsFor(d, drilling).Select(r => bound[ReportCalculator.KeyOf(r)]).ToList();
        var columns = new List<ReportColumn>();
        if (!drilling)
        {
            columns.AddRange(d.Groups.Select(g =>
            {
                var b = bound[ReportCalculator.KeyOf(g.Field)];
                return new ReportColumn(b.Key, ColumnLabel(b, locale), "group", g.Bucket is null ? ValueTypeOf(b.Field) : "text");
            }));
            columns.AddRange(d.Measures.Select(m => MeasureColumn(m, bound, mainSide, childSide, locale)));
        }
        columns.AddRange(fieldColumns.Select(b => new ReportColumn(b.Key, ColumnLabel(b, locale), "field", ValueTypeOf(b.Field))));

        IReadOnlyList<ReportGroupResult>? groups = drilling ? null : LabeledGroups(calc.Groups, d, bound, ctx);
        IReadOnlyList<IReadOnlyList<object?>>? detail = fieldColumns.Count == 0 ? null : DetailRows(rows, fieldColumns, d, bound, ctx);
        ReportAppliedRange? applied = range is null ? null : new ReportAppliedRange(resolved!.Value.Item1, resolved.Value.Item2, range.Kind, range.N);
        return new ReportResult(rows.Count == 0 ? Empty : Ok, null, $"一筆{rowModule.NameI18n.Resolve(locale)}", columns,
            groups, calc.Total, detail, calc.DedupedNote, basis, applied, frozen);
    }

    private async Task<Module?> ActiveModuleAsync(string key, CancellationToken ct) =>
        await modules.GetByKeyAsync(key, includeChildren: true, ct) is { Status: ModuleStatus.Active } m ? m : null;

    /// <summary>定義裡所有位置的欄位引用，權限判斷只看這份清單，新增位置要加在這裡。</summary>
    internal static List<ReportFieldRef> AllRefs(ReportDefinition d)
    {
        var refs = new List<ReportFieldRef>(d.Columns);
        refs.AddRange(d.Groups.Select(g => g.Field));
        refs.AddRange(d.Measures.Where(m => m.Field is not null).Select(m => m.Field!));
        refs.AddRange(d.Measures.Where(m => m.Condition is not null).Select(m => m.Condition!.Field));
        refs.AddRange(d.Filters.Select(f => f.Field));
        if (d.PrimaryDateField is not null) refs.Add(d.PrimaryDateField);
        refs.AddRange(d.Sort.Where(s => d.Measures.All(m => m.Id != s.Key))
            .Select(s => ParseFieldKey(s.Key)).Where(r => r is not null).Select(r => r!));
        return refs;
    }

    /// <summary>ReportSort.Key 不是量值 Id 時是 ReportCalculator.KeyOf 的格式；其他字串不對應任何欄位，排序時忽略。</summary>
    private static ReportFieldRef? ParseFieldKey(string key)
    {
        var parts = key.Split(':');
        if (parts.Length != 3 || !long.TryParse(parts[1], CultureInfo.InvariantCulture, out var id)) return null;
        if (parts[2].Length == 0) return new ReportFieldRef(parts[0], id, null);
        return long.TryParse(parts[2], CultureInfo.InvariantCulture, out var via) ? new ReportFieldRef(parts[0], id, via) : null;
    }

    private static (Bound?, string?) Bind(
        ReportFieldRef r, string key, Side main, Side? child, IReadOnlyDictionary<string, Side> targets, string locale)
    {
        var source = r.Source == "main" ? main : r.Source == "child" ? child : null;
        if (source is null) return (null, "欄位已不存在");
        if (r.ViaFieldId is null)
            return source.Active(r.FieldId) is { } direct
                ? (new Bound(r, key, source, direct, null, null), null)
                : (null, MissingMessage(source, r.FieldId, locale));

        if (source.Active(r.ViaFieldId) is not { Type: FieldType.Reference, Config.RefTarget: { } refTarget } via)
            return (null, MissingMessage(source, r.ViaFieldId.Value, locale));
        if (refTarget.Kind == ReferenceTarget.EntityKind)
            return r.FieldId == EntityTitleFieldId ? (new Bound(r, key, source, via, null, null), null) : (null, "欄位已不存在");
        var target = targets[refTarget.Key];
        return target.Active(r.FieldId) is { } field
            ? (new Bound(r, key, source, field, via, target), null)
            : (null, MissingMessage(target, r.FieldId, locale));
    }

    private static string MissingMessage(Side side, long fieldId, string locale) =>
        side.Perms is { } p && side.Fields.TryGetValue(fieldId, out var f) && p.CanSee(f)
            ? $"欄位已不存在：{f.LabelI18n.Resolve(locale)}"
            : "欄位已不存在";

    private static string? UnusableMessage(Bound b, string locale)
    {
        var hidden = $"無權限檢視：{HiddenFieldName}";
        var sourcePerms = b.Source.Perms!;
        if (b.Target is null)
        {
            if (!sourcePerms.CanSee(b.Field)) return hidden;
            return b.Source.Excluded.Contains(b.Field.Id) ? $"這個欄位不能用於報表：{b.Field.LabelI18n.Resolve(locale)}" : null;
        }
        if (!sourcePerms.CanSee(b.Via!)) return hidden;
        if (b.Target.Perms is { } targetPerms && !targetPerms.CanSee(b.Field)) return hidden;
        if (!b.Target.Excluded.Contains(b.Field.Id)) return null;
        return b.Target.Perms is null ? hidden : $"這個欄位不能用於報表：{b.Field.LabelI18n.Resolve(locale)}";
    }

    /// <summary>運算子沿用欄位型別允許的清單（與列表篩選相同）；定義不合法是存檔驗證的缺口，不回傳假數字。</summary>
    private void EnsureOperator(ReportFilter f, Bound b)
    {
        if (!Operators.TryGetValue(f.Op, out var op) || !registry.Get(b.Field.Type).OperatorsFor(b.Field).Contains(op))
            throw new ArgumentException($"欄位 {b.Field.Id} 不支援運算子 {f.Op}");
    }

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
    /// 帶偏移的值換成台北牆上時間（FieldDisplay.BusinessOffset，與 IBusinessTime 同一時區）；
    /// 不帶偏移的值本身就是台北牆上時間（spec §6.4）。
    /// </summary>
    private static bool TryWallClock(string s, out DateTime at)
    {
        at = default;
        // AssumeUniversal 讓解析不經過主機時區；Kind 只用來分辨字串有沒有帶偏移
        if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            || !DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) return false;
        at = parsed.Kind == DateTimeKind.Unspecified ? dto.DateTime : dto.ToOffset(FieldDisplay.BusinessOffset).DateTime;
        return true;
    }

    // ---- 條件 ----

    /// <summary>
    /// 平鋪 AND 清單裡的一個條件，語意同列表篩選：ne、notIn 包含空值，比較類不含空值。
    /// 看不到或已刪除的目標一律不符合，猜中或猜錯目標的值結果都一樣。
    /// </summary>
    private static bool Matches(ReportFilter f, ReportCell c, DateOnly basis)
    {
        if (c.State is Restricted or Missing) return false;
        var op = Operators[f.Op];
        if (op == FilterOperator.IsEmpty) return c.State == Empty;
        if (op == FilterOperator.IsNotEmpty) return c.State != Empty;
        if (c.State == Empty) return op is FilterOperator.Ne or FilterOperator.NotIn;

        var v = f.Value ?? throw new ArgumentException($"運算子 {f.Op} 需要條件值");
        // 存檔後欄位型別可能變（例如公式結果型別由數字改日期），舊條件值的形狀對不上時視為不符合，不丟例外
        try { return MatchValue(f, op, v, c, basis); }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { return false; }
    }

    private static bool MatchValue(ReportFilter f, FilterOperator op, JsonElement v, ReportCell c, DateOnly basis)
    {
        if (c.Date is { } date) return MatchDate(op, v, c is TimedCell t ? t.At : date.ToDateTime(TimeOnly.MinValue), basis);
        if (c.Number is { } number) return MatchNumber(op, v, number);

        IReadOnlyList<string> texts = c.Multi ?? [c.Text!];
        return op switch
        {
            FilterOperator.Eq => texts.Contains(Scalar(v)),
            FilterOperator.Ne => !texts.Contains(Scalar(v)),
            FilterOperator.In => v.EnumerateArray().Any(e => texts.Contains(Scalar(e))),
            FilterOperator.NotIn => !v.EnumerateArray().Any(e => texts.Contains(Scalar(e))),
            FilterOperator.Contains => texts.Any(x => x.Contains(Scalar(v), StringComparison.OrdinalIgnoreCase)),
            FilterOperator.StartsWith => texts.Any(x => x.StartsWith(Scalar(v), StringComparison.OrdinalIgnoreCase)),
            // 只有時間型別的文字值走到比較類（EnsureOperator 已擋其他型別）：HH:mm 字典序即時間序
            FilterOperator.Gt => string.CompareOrdinal(c.Text, Scalar(v)) > 0,
            FilterOperator.Gte => string.CompareOrdinal(c.Text, Scalar(v)) >= 0,
            FilterOperator.Lt => string.CompareOrdinal(c.Text, Scalar(v)) < 0,
            FilterOperator.Lte => string.CompareOrdinal(c.Text, Scalar(v)) <= 0,
            FilterOperator.Between => string.CompareOrdinal(c.Text, Scalar(v[0])) >= 0 && string.CompareOrdinal(c.Text, Scalar(v[1])) <= 0,
            _ => throw new ArgumentException($"文字值不支援運算子 {f.Op}"),
        };
    }

    private static string Scalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString()!,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => e.GetRawText(),
    };

    private static bool MatchNumber(FilterOperator op, JsonElement v, decimal n) => op switch
    {
        FilterOperator.Eq => n == v.GetDecimal(),
        FilterOperator.Ne => n != v.GetDecimal(),
        FilterOperator.Gt => n > v.GetDecimal(),
        FilterOperator.Gte => n >= v.GetDecimal(),
        FilterOperator.Lt => n < v.GetDecimal(),
        FilterOperator.Lte => n <= v.GetDecimal(),
        FilterOperator.Between => n >= v[0].GetDecimal() && n <= v[1].GetDecimal(),
        _ => throw new ArgumentException($"數字值不支援運算子 {op}"),
    };

    private static bool MatchDate(FilterOperator op, JsonElement v, DateTime at, DateOnly basis)
    {
        switch (op)
        {
            case FilterOperator.WithinNextDays: return InDays(at, basis, basis.AddDays(v.GetInt32()));
            case FilterOperator.WithinLastDays: return InDays(at, basis.AddDays(-v.GetInt32()), basis);
            case FilterOperator.Between: return at >= Span(v[0]).From && at < Span(v[1]).Until;
        }
        var (from, until) = Span(v);
        return op switch
        {
            FilterOperator.Eq => at >= from && at < until,
            FilterOperator.Ne => at < from || at >= until,
            FilterOperator.Gt => at >= until,
            FilterOperator.Gte => at >= from,
            FilterOperator.Lt => at < from,
            FilterOperator.Lte => at < until,
            _ => throw new ArgumentException($"日期值不支援運算子 {op}"),
        };
    }

    private static bool InDays(DateTime at, DateOnly from, DateOnly toInclusive) =>
        at >= from.ToDateTime(TimeOnly.MinValue) && at < toInclusive.AddDays(1).ToDateTime(TimeOnly.MinValue);

    /// <summary>條件值涵蓋的時間：只給日期是一整天，上界是隔天 00:00 之前（spec §6.4）；有時間就是那一刻。</summary>
    private static (DateTime From, DateTime Until) Span(JsonElement v)
    {
        var s = v.GetString() ?? throw new ArgumentException("日期條件值必須是文字");
        if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return (d.ToDateTime(TimeOnly.MinValue), d.AddDays(1).ToDateTime(TimeOnly.MinValue));
        if (!TryWallClock(s, out var at)) throw new FormatException($"日期條件值格式錯誤：{s}");
        return (at, at.AddTicks(1));
    }

    // ---- 輸出 ----

    private static IEnumerable<ReportFieldRef> FieldColumnsFor(ReportDefinition d, bool drilling)
    {
        if (d.Columns.Count > 0 && (drilling || d.Groups.Count == 0)) return d.Columns;
        if (!drilling) return [];
        return d.Groups.Select(g => g.Field).Concat(d.Measures.Where(m => m.Field is not null).Select(m => m.Field!))
            .DistinctBy(ReportCalculator.KeyOf);
    }

    private static string ColumnLabel(Bound b, string locale)
    {
        var field = b.Field.LabelI18n.Resolve(locale);
        if (b.Via is not null) return b.Target!.Perms is null ? HiddenFieldName : $"{b.Via.LabelI18n.Resolve(locale)}．{field}";
        return b.Ref.ViaFieldId is not null ? $"{field}．標題" : field;
    }

    private static string ValueTypeOf(Field f) => f.Type switch
    {
        FieldType.Integer or FieldType.Decimal or FieldType.Currency => "number",
        FieldType.Percent => "percent",
        FieldType.Date => "date",
        FieldType.DateTime => "dateTime",
        FieldType.Formula or FieldType.Rollup => f.Config.ResultType == "date" ? "date" : "number",
        _ => "text",
    };

    private static ReportColumn MeasureColumn(
        ReportMeasure m, IReadOnlyDictionary<string, Bound> bound, Side main, Side? child, string locale)
    {
        string FieldLabel(ReportFieldRef r) => ColumnLabel(bound[ReportCalculator.KeyOf(r)], locale);
        var (label, type) = m.Kind switch
        {
            "countMain" => ($"{main.Module.NameI18n.Resolve(locale)}筆數", "number"),
            "countChild" => ($"{(child ?? main).Module.NameI18n.Resolve(locale)}筆數", "number"),
            "ratio" => ($"{(m.Condition is null ? "" : FieldLabel(m.Condition.Field))} 條件占比", "ratio"),
            _ => ($"{(m.Field is null ? "" : FieldLabel(m.Field))} {m.Kind switch { "sum" => "加總", "avg" => "平均", "min" => "最小", "max" => "最大", _ => m.Kind }}",
                m.Field is not null && m.Kind is "min" or "max" && ValueTypeOf(bound[ReportCalculator.KeyOf(m.Field!)].Field) is "date" or "dateTime" ? "dayNumber" : "number"),
        };
        return new ReportColumn(m.Id, label, "measure", type);
    }

    private static List<ReportGroupResult> LabeledGroups(
        IReadOnlyList<ReportGroupResult> groups, ReportDefinition d, IReadOnlyDictionary<string, Bound> bound, RunContext ctx)
    {
        var fields = d.Groups.Select(g => bound[ReportCalculator.KeyOf(g.Field)]).ToList();
        var sorted = groups.ToList();
        sorted.Sort((x, y) =>
        {
            foreach (var s in d.Sort)
            {
                int c;
                if (d.Measures.Any(m => m.Id == s.Key))
                {
                    c = CompareNullable(x.Values.GetValueOrDefault(s.Key), y.Values.GetValueOrDefault(s.Key), s.Desc);
                }
                else
                {
                    var i = fields.FindIndex(f => f.Key == s.Key);
                    if (i < 0) continue;
                    c = CompareKeys(fields[i], x.GroupKeys[i], y.GroupKeys[i], s.Desc);
                }
                if (c != 0) return c;
            }
            for (var i = 0; i < fields.Count; i++)
            {
                var c = CompareKeys(fields[i], x.GroupKeys[i], y.GroupKeys[i], false);
                if (c != 0) return c;
            }
            return 0;
        });
        return sorted.Select(g => new ReportGroupResult(
            g.GroupKeys, g.GroupKeys.Select((k, i) => GroupLabel(fields[i], k, ctx)).ToList(), g.Values)).ToList();
    }

    private static int CompareNullable(decimal? x, decimal? y, bool desc)
    {
        if (x is null || y is null) return (x is null).CompareTo(y is null); // 沒有值的一律排最後
        return desc ? y.Value.CompareTo(x.Value) : x.Value.CompareTo(y.Value);
    }

    /// <summary>選項依 Sequence、數字依大小，其餘依字串；空白與已刪除、無權限固定排最後，不受方向影響。</summary>
    private static int CompareKeys(Bound b, string x, string y, bool desc)
    {
        var rank = KeyRank(b, x).CompareTo(KeyRank(b, y));
        if (rank != 0) return rank;
        var c = 0;
        if (b.Field.Type is FieldType.Enum or FieldType.MultiEnum)
            c = OptionSequence(b.Field, x).CompareTo(OptionSequence(b.Field, y));
        else if (decimal.TryParse(x, NumberStyles.Number, CultureInfo.InvariantCulture, out var nx)
                 && decimal.TryParse(y, NumberStyles.Number, CultureInfo.InvariantCulture, out var ny)
                 && ValueTypeOf(b.Field) is "number" or "percent")
            c = nx.CompareTo(ny);
        if (c == 0) c = string.CompareOrdinal(x, y);
        return desc ? -c : c;
    }

    private static int KeyRank(Bound b, string key)
    {
        if (key.Length == 0) return 1;
        if (!b.MayBeUnavailable) return 0;
        return key == ReportCalculator.MissingKey ? 2 : key == ReportCalculator.RestrictedKey ? 3 : 0;
    }

    private static int OptionSequence(Field f, string key) =>
        f.Options.FirstOrDefault(o => $"opt_{o.Id}" == key)?.Sequence ?? int.MaxValue;

    private static string GroupLabel(Bound b, string key, RunContext ctx)
    {
        if (key.Length == 0) return BlankLabel;
        if (b.MayBeUnavailable && key == ReportCalculator.MissingKey) return DeletedLabel;
        if (b.MayBeUnavailable && key == ReportCalculator.RestrictedKey) return RestrictedLabel;
        return ValueLabel(b, key, ctx);
    }

    /// <summary>Text 裡的鍵換成顯示文字：選項標籤、是否、使用者姓名、參照標題；其他型別鍵就是文字本身。</summary>
    private static string ValueLabel(Bound b, string key, RunContext ctx)
    {
        JsonElement? raw = b.Field.Type switch
        {
            FieldType.Enum or FieldType.User => JsonSerializer.SerializeToElement(key),
            FieldType.MultiEnum => JsonSerializer.SerializeToElement(new[] { key }),
            FieldType.Boolean => JsonSerializer.SerializeToElement(key == "true"),
            _ => null,
        };
        if (raw is not null) return FieldDisplay.Format(b.Field, raw, ctx.Locale, null, ctx.UserNames);
        return b.Field.Type == FieldType.Reference ? ctx.Titles.GetValueOrDefault((b.Key, key), key) : key;
    }

    private static List<IReadOnlyList<object?>> DetailRows(
        IReadOnlyList<ReportRow> rows, IReadOnlyList<Bound> columns, ReportDefinition d, IReadOnlyDictionary<string, Bound> bound, RunContext ctx)
    {
        var sorts = d.Sort.Where(s => bound.ContainsKey(s.Key) && d.Measures.All(m => m.Id != s.Key)).ToList();
        IEnumerable<ReportRow> ordered = rows;
        if (sorts.Count > 0)
            ordered = rows.OrderBy(r => r, Comparer<ReportRow>.Create((x, y) =>
            {
                foreach (var s in sorts)
                {
                    var c = CompareCells(bound[s.Key], x.Cells[s.Key], y.Cells[s.Key], s.Desc);
                    if (c != 0) return c;
                }
                return 0;
            }));
        return ordered.Select(r => (IReadOnlyList<object?>)columns.Select(b => Display(b, r.Cells[b.Key], ctx)).ToList()).ToList();
    }

    private static int CompareCells(Bound b, ReportCell x, ReportCell y, bool desc)
    {
        if (x.State != Ok || y.State != Ok) return (x.State != Ok).CompareTo(y.State != Ok);
        if (x.Number is { } nx && y.Number is { } ny) return desc ? ny.CompareTo(nx) : nx.CompareTo(ny);
        if (x.Date is { } dx && y.Date is { } dy)
        {
            var c = (x is TimedCell tx && y is TimedCell ty) ? tx.At.CompareTo(ty.At) : dx.CompareTo(dy);
            return desc ? -c : c;
        }
        return CompareKeys(b, x.Text ?? x.Multi?[0] ?? "", y.Text ?? y.Multi?[0] ?? "", desc);
    }

    /// <summary>明細一格：數字回 decimal，其餘回顯示文字，沒有值回 null。</summary>
    private static object? Display(Bound b, ReportCell c, RunContext ctx)
    {
        switch (c.State)
        {
            case Restricted: return RestrictedLabel;
            case Missing: return DeletedLabel;
            case Empty: return null;
        }
        if (c.Number is { } n) return n;
        if (c is TimedCell t) return t.At.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        if (c.Date is { } d) return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (c.Multi is { } multi) return FieldDisplay.Format(b.Field, JsonSerializer.SerializeToElement(multi), ctx.Locale, null, ctx.UserNames);
        return ValueLabel(b, c.Text!, ctx);
    }
}
