using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Domain.AutoNumbers;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Formulas;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.FieldService.Data;

public static class FieldRules
{
    public const string MaxLengthError = "長度上限只適用文字欄位，文字欄位最多 4000";
    public const string FilterError = "此欄位型別不支援篩選";
    public const string SortError = "此欄位型別不支援排序";
    public const string ComputedError = "系統計算欄位不能設定必填、預設值、唯讀或長度";
    public const string AutoNumberFormatError = "自動編號格式不正確";
    public const string ResultTypeError = "結果型別必須是 number、currency 或 date";
    public const string ScaleError = "小數位數只適用數值欄位，範圍 0 到 6";

    public const string ReferenceFixedError = "參照欄位不能設定預設值、長度、小數位、格式或排序";
    public const string RefTargetError = "參照目標不正確";
    public const string RefTargetLockedError = "參照目標建立後不能更改";
    public const string OnDeleteTargetError = "刪除方式只能用在參照動態模組的欄位";
    public const string SetNullRequiredError = "刪除時清空不能用在必填欄位";

    public const string RelationLockedError = "明細設定建立後不能更改";
    public const string ScopeError = "只有明細模組的自動編號可以在表頭內編號";
    public const string ScopeFormatError = "在表頭內編號的格式不能含日期";
    public const string ScopeLockedError = "在表頭內編號建立後不能更改";

    public const string DefaultTodayTypeError = "只有日期欄位可以預設今天";
    public const string DefaultTodayExclusiveError = "預設今天與預設值只能擇一";
    public const string ParentDefaultNotDetailError = "只有明細模組的欄位可以帶表頭的值";
    public const string ParentDefaultFieldError = "表頭欄位不存在、未啟用或型別不同";
    public const string ParentDefaultExclusiveError = "帶表頭的值不能同時設定預設值或預設今天";

    private static readonly HashSet<FieldType> ParentDefaultTypes =
    [
        FieldType.Text, FieldType.Textarea, FieldType.Integer, FieldType.Decimal, FieldType.Currency, FieldType.Percent,
        FieldType.Date, FieldType.DateTime, FieldType.Time, FieldType.Boolean, FieldType.Email, FieldType.Phone,
        FieldType.Url, FieldType.User
    ];

    public static IRuleBuilderOptions<T, I18nText> ValidLabel<T>(this IRuleBuilder<T, I18nText> rule) =>
        rule.Must(n => n is not null && n.IsValid(out _)).WithMessage("名稱必須有繁體中文");

    /// <summary>系統計算欄位：公式、自動編號與彙總，值由系統產生、不接受輸入。</summary>
    public const string AttachmentInDetailError = "明細模組不支援附件欄位，請放在表頭模組";

    public static bool IsComputed(FieldType type) => type is FieldType.Formula or FieldType.AutoNumber or FieldType.Rollup;

    /// <summary>依型別檢查 MaxLength、Scale、IsFilterable、IsSortable；DefaultValue 需要選項與使用者，留給 handler。</summary>
    public static void ValidateConfig<T>(FieldType type, FieldConfig? config, ValidationContext<T> ctx)
    {
        if (config is null)
        {
            ctx.AddFailure("Config", "必須提供欄位設定");
            return;
        }

        if (type == FieldType.Reference)
        {
            if (config.DefaultValue is not null || config.MaxLength is not null || config.Precision is not null
                || config.Scale is not null || config.Format is not null || config.Formula is not null || config.IsSortable)
                ctx.AddFailure("Config", ReferenceFixedError);
            return;
        }

        if (IsComputed(type))
        {
            if (config.Required || config.ReadOnly || config.DefaultValue is not null || config.MaxLength is not null)
                ctx.AddFailure("Config", ComputedError);
            if (type == FieldType.AutoNumber && !AutoNumberFormat.TryParse(config.Format, out _))
                ctx.AddFailure("Config.Format", AutoNumberFormatError);
            if (type == FieldType.Formula)
            {
                if (config.ResultType is not ("number" or "currency" or "date"))
                    ctx.AddFailure("Config.ResultType", ResultTypeError);
            }
        }

        if (config.MaxLength is { } max && !IsComputed(type))
        {
            var limit = type switch { FieldType.Text => 4000, FieldType.Textarea => int.MaxValue, _ => 0 };
            if (max < 1 || max > limit) ctx.AddFailure("Config.MaxLength", MaxLengthError);
        }

        var scaleAllowed = type is FieldType.Decimal or FieldType.Currency or FieldType.Percent
            || (type == FieldType.Formula && config.ResultType != "date")
            || type == FieldType.Rollup; // 彙總的 Scale 由伺服器推得覆蓋，這裡只擋範圍
        if (config.Scale is { } scale && (!scaleAllowed || scale is < 0 or > 6))
            ctx.AddFailure("Config.Scale", ScaleError);

        if (config.IsFilterable && !FieldTypeCapabilities.SupportsFilter(type))
            ctx.AddFailure("Config.IsFilterable", FilterError);

        if (config.IsSortable && !FieldTypeCapabilities.SupportsSort(type))
            ctx.AddFailure("Config.IsSortable", SortError);
    }

    /// <summary>目標必須是存在的 Standalone 模組，或已登錄的實體。</summary>
    public static async Task<bool> IsValidRefTargetAsync(
        ReferenceTarget? target, IModuleRepository modules, ReferenceProviderRegistry providers, CancellationToken ct) =>
        target switch
        {
            { Kind: ReferenceTarget.ModuleKind } => await modules.GetByKeyAsync(target.Key, includeChildren: true, ct)
                is { Kind: ModuleKind.Standalone } t && !ModuleRelations.IsDetail(t),
            { Kind: ReferenceTarget.EntityKind } => providers.Get(target.Key) is not null,
            _ => false
        };

    /// <summary>參照欄位的目標與刪除方式；targetOk 由 handler 查好。</summary>
    public static ServiceResult? CheckReference(FieldConfig config, bool targetOk)
    {
        if (config.RefTarget is null || !targetOk) return Invalid("Config.RefTarget", RefTargetError);
        if (config.OnDelete is not null && config.RefTarget.Kind != ReferenceTarget.ModuleKind)
            return Invalid("Config.OnDelete", OnDeleteTargetError);
        if (config.OnDelete == ReferenceOnDelete.SetNull && config.Required)
            return Invalid("Config.OnDelete", SetNullRequiredError);
        return null;
    }

    /// <summary>
    /// 參照欄位要設為明細時的檢查，依序回報第一個成立的原因。目標不存在等一般錯誤放行，交給 CheckReference。
    /// module 是欄位所在的模組（未來的明細模組）。
    /// </summary>
    public static async Task<ServiceResult?> CheckDetailAsync(
        Module module, FieldConfig config, IModuleRepository modules, IRecordRepository records, CancellationToken ct)
    {
        static ServiceResult NotDetail(string reason) => Invalid("Config.Relation", $"這個欄位不能設為明細：{reason}");

        if (config.RefTarget is null) return null;
        if (config.RefTarget.Kind == ReferenceTarget.EntityKind) return NotDetail("只能參照動態模組");
        if (config.RefTarget.Kind != ReferenceTarget.ModuleKind) return null;
        if (await modules.GetByKeyAsync(config.RefTarget.Key, includeChildren: true, ct) is not { } parent) return null;

        if (parent.Kind == ModuleKind.Extension || ModuleRelations.IsDetail(parent) || parent.Id == module.Id)
            return NotDetail("表頭模組不能是擴充模組或其他模組的明細");
        if (module.Blocks.SelectMany(b => b.Fields).Any(f => f is { Type: FieldType.Reference, Config.Relation: ReferenceRelation.Detail }))
            return NotDetail("這個模組已經有表頭");
        if (await records.AnyByModuleAsync(module.Id, ct)) return NotDetail("這個模組已經有資料");
        if ((await modules.ListDetailModulesAsync(module.Key, ct)).Count > 0) return NotDetail("這個模組有自己的明細，不能再當明細");
        if (await modules.IsReferencedAsync(module.Key, ct)) return NotDetail("這個模組被其他欄位參照");
        if (module.Kind == ModuleKind.Extension) return NotDetail("擴充模組不能當明細");
        return null;
    }

    /// <summary>Scope 只能是 parent，且只能用在明細模組的自動編號；表頭內編號的格式不能含日期。</summary>
    public static ServiceResult? CheckScope(Module module, FieldType type, FieldConfig config)
    {
        if (config.Scope is null) return null;
        if (config.Scope != "parent" || type != FieldType.AutoNumber || !ModuleRelations.IsDetail(module))
            return Invalid("Config.Scope", ScopeError);
        if (AutoNumberFormat.TryParse(config.Format, out var format) && format.PeriodKey(new DateTime(2000, 1, 1)).Length > 0)
            return Invalid("Config.Format", ScopeFormatError);
        return null;
    }

    /// <summary>
    /// 彙總欄位：來源、彙總對象、函數、條件依序檢查，通過時回傳寫入 ResultType、Scale 後的設定。
    /// module 是彙總欄位所在的模組（父記錄模組）。
    /// </summary>
    public static async Task<(ServiceResult? Error, FieldConfig Config)> CheckRollupAsync(
        Module module, FieldConfig config, IModuleRepository modules, FieldTypeRegistry registry, CancellationToken ct)
    {
        static (ServiceResult?, FieldConfig) Bad(FieldConfig c, string reason) =>
            (Invalid("Config.Rollup", $"彙總設定不正確：{reason}"), c);

        const string RollupOfRollup = "不能彙總另一個彙總欄位或引用它的公式";
        var cfg = config.Rollup;
        var source = cfg is null ? null : await modules.GetByFieldIdAsync(cfg.SourceFieldId, ct);
        var sourceField = source?.Blocks.SelectMany(b => b.Fields).Single(f => f.Id == cfg!.SourceFieldId);
        if (cfg is null || source is null || sourceField is not
            { Type: FieldType.Reference, Status: FieldStatus.Active, Config.RefTarget: { Kind: ReferenceTarget.ModuleKind } target }
            || target.Key != module.Key)
            return Bad(config, "來源欄位必須是指向這個模組的參照欄位");

        if (cfg.Function == RollupFunction.Count)
        {
            if (cfg.AggregateFieldId is not null) return Bad(config, "筆數不需要彙總欄位");
            return (null, config with { ResultType = "number", Scale = 0 });
        }
        if (cfg.AggregateFieldId is null) return Bad(config, "必須指定彙總欄位");

        var aggregate = source.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == cfg.AggregateFieldId);
        if (aggregate is not { Status: FieldStatus.Active }) return Bad(config, "彙總欄位必須是來源模組的數字或日期欄位");
        if (ModuleRelations.DependsOnRollup(source, aggregate)) return Bad(config, RollupOfRollup);
        if (aggregate.Type is not (FieldType.Integer or FieldType.Decimal or FieldType.Currency or FieldType.Percent
            or FieldType.Date or FieldType.Formula))
            return Bad(config, "彙總欄位必須是來源模組的數字或日期欄位");

        var isDate = aggregate.Type == FieldType.Date || (aggregate.Type == FieldType.Formula && aggregate.Config.ResultType == "date");
        if (isDate && cfg.Function == RollupFunction.Sum) return Bad(config, "加總不能用在日期欄位");

        if (cfg.Condition is { ValueKind: JsonValueKind.Object } condition)
        {
            var parsed = ConditionParser.Parse(condition, source, registry, ConditionUsage.Rule, await modules.ListUserIdsAsync(ct));
            if (parsed.Error is not null) return Bad(config, $"條件不正確：{parsed.Error}");
            if (parsed.Node is not null && ConditionFields.Collect(parsed.Node).Any(f => ModuleRelations.DependsOnRollup(source, f)))
                return Bad(config, "條件不正確：不能使用彙總欄位或引用它的公式");
        }

        var isCurrency = aggregate.Type == FieldType.Currency || (aggregate.Type == FieldType.Formula && aggregate.Config.ResultType == "currency");
        return (null, config with
        {
            ResultType = isCurrency ? "currency" : isDate ? "date" : "number",
            Scale = isDate ? null : aggregate.Config.Scale ?? (aggregate.Type == FieldType.Integer ? 0 : 2)
        });
    }

    /// <summary>用該型別處理器正規化預設值；失敗回 400（鍵 Config.DefaultValue），成功回正規化後的設定。</summary>
    public static ServiceResult? CheckDefault(
        FieldTypeRegistry registry, Field field, FieldConfig config, HashSet<string> userIds, out FieldConfig normalized)
    {
        normalized = config;
        if (config.DefaultValue is null) return null;

        var active = field.Options.Where(o => o.Active).Select(o => o.Id).ToHashSet();
        var all = field.Options.Select(o => o.Id).ToHashSet();
        var sequence = field.Options.ToDictionary(o => o.Id, o => o.Sequence);
        var ctx = new FieldTypeContext(active, all, sequence, field.Config.DefaultValue, userIds);
        var probe = new Field { Id = field.Id, Type = field.Type, Config = config, Options = field.Options };
        var result = registry.Get(field.Type).Normalize(config.DefaultValue, probe, ctx);
        if (!result.IsValid)
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["Config.DefaultValue"] = [result.Error!] });

        normalized = config with { DefaultValue = result.Value };
        return null;
    }

    /// <summary>module 是明細模組時回它的表頭模組，否則 null。</summary>
    public static async Task<Module?> ParentOfAsync(Module module, IModuleRepository modules, CancellationToken ct) =>
        ModuleRelations.ParentKeyOf(module) is { } key ? await modules.GetByKeyAsync(key, includeChildren: true, ct) : null;

    /// <summary>預設今天與帶表頭的值；parent 是明細模組的表頭，非明細模組傳 null。</summary>
    public static ServiceResult? CheckDynamicDefaults(Module module, Field field, FieldConfig config, Module? parent)
    {
        if (config.DefaultToday)
        {
            if (field.Type != FieldType.Date) return Invalid("Config.DefaultToday", DefaultTodayTypeError);
            if (config.DefaultValue is not null) return Invalid("Config.DefaultToday", DefaultTodayExclusiveError);
        }
        if (config.ParentDefaultFieldId is not { } parentFieldId) return null;

        if (parent is null || !ModuleRelations.IsDetail(module))
            return Invalid("Config.ParentDefaultFieldId", ParentDefaultNotDetailError);
        if (config.DefaultValue is not null || config.DefaultToday)
            return Invalid("Config.ParentDefaultFieldId", ParentDefaultExclusiveError);
        var source = parent.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == parentFieldId);
        if (source is not { Status: FieldStatus.Active } || source.Type != field.Type || !ParentDefaultTypes.Contains(field.Type))
            return Invalid("Config.ParentDefaultFieldId", ParentDefaultFieldError);
        return null;
    }

    public const string FillKey = "Config.Fill";

    /// <summary>
    /// 帶值設定（Config.Fill）：本欄必須是參照動態模組的欄位；每筆對應的來源欄位屬於目標模組、目的欄位屬於本模組，
    /// 型別相容、目的欄位不重複也不是系統計算欄位；lock 模式的目的欄位必須是唯讀。fieldId 為 0 表示新欄位。
    /// </summary>
    public static async Task<ServiceResult?> CheckFillAsync(
        Module module, Field field, long fieldId, FieldConfig config, IModuleRepository modules, CancellationToken ct)
    {
        if (config.Fill is not { } fill) return null;
        if (field.Type != FieldType.Reference || config.RefTarget is not { Kind: ReferenceTarget.ModuleKind } target)
            return Invalid(FillKey, "只有參照動態模組的欄位可以設定帶值");
        if (fill.Mode is not (FillConfig.Lock or FillConfig.Default)) return Invalid(FillKey, "帶值方式只能是 lock 或 default");
        if (fill.Map is not { Count: > 0 and <= 30 }) return Invalid(FillKey, "帶值至少要有一筆對應，最多 30 筆");
        var source = await modules.GetByKeyAsync(target.Key, includeChildren: true, ct);
        if (source is null) return Invalid(FillKey, "找不到參照目標模組");

        var from = source.Blocks.SelectMany(b => b.Fields).Where(f => f.Status == FieldStatus.Active).ToDictionary(f => f.Id);
        var to = module.Blocks.SelectMany(b => b.Fields).Where(f => f.Status == FieldStatus.Active).ToDictionary(f => f.Id);
        var seen = new HashSet<long>();
        foreach (var m in fill.Map)
        {
            if (!from.TryGetValue(m.From, out var src)) return Invalid(FillKey, "來源欄位不屬於參照目標模組，或未啟用");
            if (!to.TryGetValue(m.To, out var dst) || (fieldId != 0 && dst.Id == fieldId))
                return Invalid(FillKey, "目的欄位不屬於本模組、未啟用，或是參照欄位自己");
            if (!seen.Add(m.To)) return Invalid(FillKey, "同一個目的欄位不能有兩筆對應");
            if (IsComputed(dst.Type) || dst.Type == FieldType.Reference) return Invalid(FillKey, "目的欄位不能是系統計算欄位或參照欄位");
            if (!CopyCompatible(src, dst)) return Invalid(FillKey, "來源與目的欄位型別不相容");
            if (fill.Mode == FillConfig.Lock && !dst.Config.ReadOnly)
                return Invalid(FillKey, $"鎖定帶值的目的欄位必須先設為唯讀：{dst.LabelI18n.Resolve(I18nText.FallbackLocale)}");
        }
        return null;
    }

    /// <summary>兩邊型別相同；自動編號可帶進文字；公式、彙總依結果型別帶進同類欄位。選項不能帶（選項 Id 每個模組不同）；參照只有轉換可以帶（目標相同）。</summary>
    public static bool CopyCompatible(Field from, Field to, bool allowReference = false)
    {
        if (from.Type == FieldType.Reference)
            return allowReference && to.Type == FieldType.Reference && from.Config.RefTarget == to.Config.RefTarget;
        if (ParentDefaultTypes.Contains(from.Type)) return from.Type == to.Type;
        return from.Type switch
        {
            FieldType.AutoNumber => to.Type == FieldType.Text,
            FieldType.Formula or FieldType.Rollup => from.Config.ResultType switch
            {
                "date" => to.Type == FieldType.Date,
                "currency" => to.Type == FieldType.Currency,
                "number" => to.Type is FieldType.Integer or FieldType.Decimal or FieldType.Percent,
                _ => false
            },
            _ => false
        };
    }

    /// <summary>公式欄位：解析、結果型別、循環引用依序檢查；module 需是已有 Id 的已存檔模組。</summary>
    public static ServiceResult? CheckFormula(Module module, long fieldId, FieldConfig config)
    {
        var parsed = FormulaParser.Parse(config.Formula, module);
        if (parsed.Error is not null) return Invalid("Config.Formula", $"公式不正確：{parsed.Error}");
        var matches = parsed.Type == FormulaValueType.Date
            ? config.ResultType == "date"
            : config.ResultType is "number" or "currency";
        if (!matches) return Invalid("Config.ResultType", "公式不正確：結果型別與設定不符");
        if (fieldId != 0 && FormulaGraph.HasCycle(module, fieldId, parsed.FieldRefs))
            return Invalid("Config.Formula", "公式有循環引用");
        return null;
    }

    public static ServiceResult Invalid(string key, string message) =>
        ServiceResult.Invalid(new Dictionary<string, string[]> { [key] = [message] });
}
