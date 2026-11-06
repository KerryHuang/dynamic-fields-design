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
public partial class ReportRunner(
    IModuleRepository modules, IRecordRepository records, IPermissionRepository permissions, IDynamicAccessGuard guard,
    ReferenceResolver resolver, FieldTypeRegistry registry, IBusinessTime time, ReportOptions options,
    AuthSettings auth, ISystemSettingRepository settings)
{
    public const long EntityTitleFieldId = 0;

    private const string Ok = "ok", Empty = "empty", Missing = "missing", Restricted = "restricted";
    private const string FieldMissingState = "fieldMissing", ModuleDisabledState = "moduleDisabled", TooManyState = "tooMany";

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
        // 卡片自己的排序取代報表的；排序鍵也走 AllRefs，用到看不到的欄位照樣整張擋下
        if (o.SortKey is not null) d = d with { Sort = [new ReportSort(o.SortKey, o.SortDesc)] };
        var basis = await BasisDateAsync(ct);
        var frozen = basis != time.Today;
        var locale = string.IsNullOrEmpty(o.Locale) ? I18nText.FallbackLocale : o.Locale;
        ReportResult Fail(string state, string message) => new(state, message, "", [], null, null, null, false, basis, null, frozen);
        string T(string key, params object[] args) => ReportText.Get(locale, key, args);
        var disabled = () => Fail(ModuleDisabledState, T(ReportText.ModuleDisabled));
        var hiddenField = () => Fail(Restricted, HiddenMessage(locale));

        // 1. 模組存在且啟用；明細模組不能當主模組（它的權限來自表頭，單獨跑會繞過表頭範圍）
        var main = await ActiveModuleAsync(d.MainModuleKey, ct);
        if (main is null) return disabled();
        if (ModuleRelations.IsDetail(main)) return Fail(ModuleDisabledState, T(ReportText.DetailAsMain));
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
        if (mainAccess.Permissions is null) return Fail(Restricted, T(ReportText.NoModuleRead));
        var mainSide = new Side(main, mainAccess.Permissions);
        Side? childSide = null;
        if (child is not null)
        {
            var access = childParent is not null
                ? await guard.AuthorizeDetailAsync(childParent, child, PermissionAction.Read, ct)
                : await guard.AuthorizeAsync(child, PermissionAction.Read, ct);
            if (access.Permissions is null) return Fail(Restricted, T(ReportText.NoChildRead));
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
        if (link is not null && !childSide!.Perms!.CanSee(link)) return hiddenField();
        foreach (var b in bound.Values)
            if (UnusableMessage(b, locale) is { } message) return Fail(Restricted, message);

        // 條件與量值用到不可讀模組的帶出欄位，結果會是假的 empty、0% 或 null，整張擋下（spec §7、§13.2）；
        // 欄位與分組位置照常，值標無權限。範圍內單筆看不到的目標仍是逐列不符合，見 Matches。
        var conditionRefs = d.Filters.Select(f => f.Field).Concat(d.Measures.Where(m => m.Condition is not null).Select(m => m.Condition!.Field)).ToList();
        var computedRefs = conditionRefs.Concat(d.Measures.Where(m => m.Field is not null).Select(m => m.Field!));
        if (d.PrimaryDateField is not null) computedRefs = computedRefs.Append(d.PrimaryDateField);
        if (computedRefs.Any(r => bound[ReportCalculator.KeyOf(r)].Target is { Perms: null }))
            return hiddenField();

        // 參照欄位本身放在條件裡同理：目標整個看不到時每一格都是 restricted，條件永遠不符合
        foreach (var f in conditionRefs.Select(r => bound[ReportCalculator.KeyOf(r)].Field).Where(f => f.Type == FieldType.Reference).DistinctBy(f => f.Id))
        {
            if (f.Config.RefTarget is not { Kind: ReferenceTarget.ModuleKind } refTarget) continue;
            var target = await ActiveModuleAsync(refTarget.Key, ct);
            if (target is null) return disabled();
            if ((await guard.AuthorizeAsync(target, PermissionAction.Read, ct)).Permissions is not { } perms || !target.TitleVisibleTo(perms))
                return hiddenField();
        }

        // 權限都過了才重驗型別，訊息只會帶看得到的名稱；公式的結果型別在存檔後可能變，對不上就明講，不回假的 empty、0% 或日期序號加總
        if (FieldWithChangedType(d, bound) is { } changed)
            return Fail(FieldMissingState, T(ReportText.TypeChanged, ColumnLabel(changed, locale)));

        // 5. 載入：多取一筆才分得出是否超過上限；超過就不回部分結果。條件要載入後才能套用，所以這個上限縮小條件也過不了，訊息要照實說
        var tooManyToLoad = () => Fail(TooManyState, T(ReportText.TooManyToLoad));
        var mainRows = await records.ListForReportAsync(main.Id, mainSide.Perms!.OwnerUserIds, options.MaxLoadRows + 1, ct);
        if (mainRows.Count > options.MaxLoadRows) return tooManyToLoad();
        IReadOnlyList<Record> childRows = [];
        if (childSide is not null)
        {
            childRows = await records.ListForReportAsync(childSide.Module.Id, childSide.Perms!.OwnerUserIds, options.MaxLoadRows + 1, ct);
            if (childRows.Count > options.MaxLoadRows) return tooManyToLoad();
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
        (DateOnly, DateOnly)? resolved = d.PrimaryDateField is null ? null
            : o.Window is { } fixedWindow ? fixedWindow
            : range is null ? null : ReportDates.Resolve(range, basis);
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
        // 合併且套用條件後的列數才是 spec §5 的上限，這裡縮小條件有用
        if (rows.Count > options.MaxRows) return Fail(TooManyState, T(ReportText.TooMany));

        // 7. 下鑽只留該分組的列；各欄位值的 key 判斷與分組相同
        var drilling = o.DrillGroupKeys is not null;
        if (o.DrillGroupKeys is { } drill)
            rows = drill.Count != d.Groups.Count
                ? []
                : rows.Where(r => d.Groups.Select((g, i) => ReportCalculator.KeysOf(r.Cells[ReportCalculator.KeyOf(g.Field)]).Contains(drill[i])).All(x => x)).ToList();

        var multiGroup = d.Groups.Any(g => bound[ReportCalculator.KeyOf(g.Field)].Field.Type == FieldType.MultiEnum);
        var calc = ReportCalculator.Calculate(rows, drilling ? [] : d.Groups, d.Measures, d.Child is not null, (f, c) => Matches(f, c, basis), multiGroup);

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
        return new ReportResult(rows.Count == 0 ? Empty : Ok, null, T(ReportText.RowMeaning, rowModule.NameI18n.Resolve(locale)), columns,
            groups, calc.Total, detail, calc.DedupedNote, basis, applied, frozen, detail?.Count);
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
        if (source is null) return (null, ReportText.Get(locale, ReportText.FieldGone));
        if (r.ViaFieldId is null)
            return source.Active(r.FieldId) is { } direct
                ? (new Bound(r, key, source, direct, null, null), null)
                : (null, MissingMessage(source, r.FieldId, locale));

        if (source.Active(r.ViaFieldId) is not { Type: FieldType.Reference, Config.RefTarget: { } refTarget } via)
            return (null, MissingMessage(source, r.ViaFieldId.Value, locale));
        if (refTarget.Kind == ReferenceTarget.EntityKind)
            return r.FieldId == EntityTitleFieldId ? (new Bound(r, key, source, via, null, null), null) : (null, ReportText.Get(locale, ReportText.FieldGone));
        var target = targets[refTarget.Key];
        return target.Active(r.FieldId) is { } field
            ? (new Bound(r, key, source, field, via, target), null)
            : (null, MissingMessage(target, r.FieldId, locale));
    }

    private static string MissingMessage(Side side, long fieldId, string locale) =>
        side.Perms is { } p && side.Fields.TryGetValue(fieldId, out var f) && p.CanSee(f)
            ? ReportText.Get(locale, ReportText.FieldGoneNamed, f.LabelI18n.Resolve(locale))
            : ReportText.Get(locale, ReportText.FieldGone);

    private static string HiddenMessage(string locale) =>
        ReportText.Get(locale, ReportText.NoPermissionTo, ReportText.Get(locale, ReportText.HiddenField));

    private static string? UnusableMessage(Bound b, string locale)
    {
        var hidden = HiddenMessage(locale);
        string NotUsable() => ReportText.Get(locale, ReportText.NotUsable, b.Field.LabelI18n.Resolve(locale));
        var sourcePerms = b.Source.Perms!;
        if (b.Target is null)
        {
            if (!sourcePerms.CanSee(b.Field)) return hidden;
            return b.Source.Excluded.Contains(b.Field.Id) ? NotUsable() : null;
        }
        if (!sourcePerms.CanSee(b.Via!)) return hidden;
        if (b.Target.Perms is { } targetPerms && !targetPerms.CanSee(b.Field)) return hidden;
        if (!b.Target.Excluded.Contains(b.Field.Id)) return null;
        return b.Target.Perms is null ? hidden : NotUsable();
    }

    /// <summary>定義裡第一個對不上欄位目前型別的位置：量值種類、主要日期、分桶、條件的運算子與值形狀。全部對得上回 null。</summary>
    private Bound? FieldWithChangedType(ReportDefinition d, IReadOnlyDictionary<string, Bound> bound)
    {
        Bound Of(ReportFieldRef r) => bound[ReportCalculator.KeyOf(r)];
        foreach (var m in d.Measures.Where(m => m.Field is not null))
        {
            var b = Of(m.Field!);
            if ((m.Kind is "sum" or "avg" && !ReportFieldEligibility.CanSum(b.Field))
                || (m.Kind is "min" or "max" && !ReportFieldEligibility.CanMinMax(b.Field))) return b;
        }
        if (d.PrimaryDateField is not null && !ReportRules.IsDateLike(Of(d.PrimaryDateField).Field)) return Of(d.PrimaryDateField);
        foreach (var g in d.Groups.Where(g => g.Bucket is not null))
            if (!ReportRules.IsDateLike(Of(g.Field).Field)) return Of(g.Field);
        foreach (var f in d.Filters.Concat(d.Measures.Where(m => m.Condition is not null).Select(m => m.Condition!)))
            if (!ConditionFits(f, Of(f.Field).Field)) return Of(f.Field);
        return null;
    }
}
