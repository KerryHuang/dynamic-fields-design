using System.Text.RegularExpressions;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.FieldService.Data;
using DynamicModules.Application.Dynamic.ConversionService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ConversionService;

/// <summary>轉換定義的結構驗證（設計器儲存時）；錯誤鍵用 definition.* 路徑。</summary>
public class ConversionValidator(IModuleRepository modules)
{
    public const string BadKey = "轉換代碼只能用小寫英文、數字、連字號，2 到 40 字，英文開頭";

    public async Task<ServiceResult?> ValidateAsync(ConversionInput input, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        void Err(string key, string message) => errors.TryAdd(key, [message]);

        if (!Regex.IsMatch(input.Key ?? "", ModuleRules.ModuleKeyPattern)) Err("key", BadKey);
        if (input.Name is null || !input.Name.IsValid(out _)) Err("name", "名稱必須有繁體中文");
        if (input.Definition is null) return ServiceResult.Invalid(new Dictionary<string, string[]> { ["definition"] = ["缺少轉換定義"] });

        var source = await modules.GetByKeyAsync(input.SourceModuleKey, includeChildren: true, ct);
        var target = await modules.GetByKeyAsync(input.TargetModuleKey, includeChildren: true, ct);
        if (source is null || target is null) Err("sourceModuleKey", "來源或目標模組不存在");
        else if (!IsDocument(source) || !IsDocument(target) || source.Id == target.Id)
            Err("targetModuleKey", "來源與目標必須是不同的獨立模組，且不是明細模組");
        if (errors.Count > 0 || source is null || target is null) return ServiceResult.Invalid(errors);

        await CheckDefinitionAsync(source, target, input.Definition, errors, ct);
        return errors.Count > 0 ? ServiceResult.Invalid(errors) : null;
    }

    /// <summary>
    /// 執行轉換前重驗已存的定義（設計器存檔之後，追溯欄位可能被停用、或被別的設定佔用）：與存檔時同一套判斷，不複製規則。
    /// 傳入的來源與目標要已載入區塊與欄位。
    /// </summary>
    public async Task<bool> IsDefinitionValidAsync(Module source, Module target, ConversionDefinition definition, CancellationToken ct)
    {
        if (!IsDocument(source) || !IsDocument(target) || source.Id == target.Id) return false;
        var errors = new Dictionary<string, string[]>();
        await CheckDefinitionAsync(source, target, definition, errors, ct);
        return errors.Count == 0;
    }

    private async Task CheckDefinitionAsync(Module source, Module target, ConversionDefinition def, Dictionary<string, string[]> errors, CancellationToken ct)
    {
        void Err(string key, string message) => errors.TryAdd(key, [message]);
        if (def.SourceStates is { Count: > 0 })
        {
            var states = source.Workflow?.States.Select(s => s.OptionId).ToHashSet() ?? [];
            if (def.SourceStates.Any(s => !states.Contains(s))) Err("definition.sourceStates", "來源狀態必須是來源模組狀態機裡的狀態");
        }

        var sourceFields = ActiveFields(source);
        var targetFields = ActiveFields(target);
        var traceKeys = new HashSet<long>();

        // 表頭：目標表頭上指向來源的唯讀參照欄位
        var header = def.Header;
        if (header is null)
        {
            Err("definition.header", "缺少表頭設定");
            return;
        }
        var trace = targetFields.GetValueOrDefault(header.SourceFieldId);
        if (trace is not { Type: FieldType.Reference } || trace.Config.RefTarget is not { Kind: ReferenceTarget.ModuleKind } rt || rt.Key != source.Key
            || trace.Config.Relation == ReferenceRelation.Detail || !trace.Config.ReadOnly)
            Err("definition.header.sourceFieldId", "必須是目標表頭上、指向來源模組、設為唯讀的參照欄位");
        else traceKeys.Add(trace.Id); // 表頭追溯欄位是參照欄位，設計器本來就不允許它當帶值的目的欄位
        CheckCopy(header.Copy, sourceFields, targetFields, traceKeys, "definition.header.copy", errors);
        CheckFixedValues(header, targetFields, traceKeys, errors);

        if (def.Lines is { } lines) await CheckLinesAsync(lines, source, target, traceKeys, errors, ct);
    }

    public async Task<(Module Source, Module Target)> ModulesOfAsync(ConversionInput input, CancellationToken ct) =>
        ((await modules.GetByKeyAsync(input.SourceModuleKey, includeChildren: false, ct))!,
         (await modules.GetByKeyAsync(input.TargetModuleKey, includeChildren: false, ct))!);

    private async Task CheckLinesAsync(ConversionLines lines, Module source, Module target, HashSet<long> traceKeys, Dictionary<string, string[]> errors, CancellationToken ct)
    {
        void Err(string key, string message) => errors.TryAdd(key, [message]);
        var sourceLine = await modules.GetByKeyAsync(lines.SourceModuleKey ?? "", includeChildren: true, ct);
        var targetLine = await modules.GetByKeyAsync(lines.TargetModuleKey ?? "", includeChildren: true, ct);
        if (sourceLine is null || ModuleRelations.ParentKeyOf(sourceLine) != source.Key)
        {
            Err("definition.lines.sourceModuleKey", "必須是來源模組的明細模組");
            return;
        }
        if (targetLine is null || ModuleRelations.ParentKeyOf(targetLine) != target.Key)
        {
            Err("definition.lines.targetModuleKey", "必須是目標模組的明細模組");
            return;
        }
        var from = ActiveFields(sourceLine);
        var to = ActiveFields(targetLine);

        var fillTargets = FillTargets(targetLine);
        var trace = to.GetValueOrDefault(lines.SourceLineFieldId);
        if (trace is not { Type: FieldType.Integer } || !trace.Config.ReadOnly)
            Err("definition.lines.sourceLineFieldId", "必須是目標明細上、設為唯讀的整數欄位（存來源明細列的 Id）");
        else if (fillTargets.Contains(trace.Id))
            Err("definition.lines.sourceLineFieldId", FillTargetMessage);
        else traceKeys.Add(trace.Id);

        var qty = lines.Quantity;
        if (qty is null || !from.TryGetValue(qty.From, out var qf) || !to.TryGetValue(qty.To, out var qt)
            || qf.Type is not (FieldType.Integer or FieldType.Decimal) || qt.Type is not (FieldType.Integer or FieldType.Decimal))
            Err("definition.lines.quantity", "數量欄位兩邊都必須是整數或小數欄位");
        else if (qt.Id == lines.SourceLineFieldId)
            Err("definition.lines.quantity", "數量欄位不能和追溯欄位是同一欄");
        else if (fillTargets.Contains(qt.Id))
            Err("definition.lines.quantity", FillTargetMessage);
        else traceKeys.Add(qt.Id);

        CheckCopy(lines.Copy, from, to, traceKeys, "definition.lines.copy", errors);
    }

    private static void CheckCopy(
        List<FillMapping>? copy, Dictionary<long, Field> from, Dictionary<long, Field> to, HashSet<long> protectedTargets,
        string path, Dictionary<string, string[]> errors)
    {
        if (copy is null) return;
        var seen = new HashSet<long>();
        foreach (var m in copy)
        {
            if (!from.TryGetValue(m.From, out var src) || !to.TryGetValue(m.To, out var dst))
            {
                errors.TryAdd(path, ["欄位對應的來源或目的欄位不存在，或未啟用"]);
                return;
            }
            if (!seen.Add(m.To) || protectedTargets.Contains(m.To) || FieldRules.IsComputed(dst.Type))
            {
                errors.TryAdd(path, ["目的欄位不能重複，也不能是追溯欄位、數量欄位或系統計算欄位"]);
                return;
            }
            if (!FieldRules.CopyCompatible(src, dst, allowReference: true))
            {
                errors.TryAdd(path, ["來源與目的欄位型別不相容"]);
                return;
            }
        }
    }

    public const string FixedValueMessage = "固定值只能寫入目標表頭啟用中的下拉欄位與它的選項，欄位不能重複，也不能是追溯欄位或複製的目的欄位";

    /// <summary>固定值（共用主檔設計 §4.5 的 header.set）：目標表頭的下拉欄位與它的選項，一個欄位只設一次，不和複製或追溯欄位重疊。</summary>
    private static void CheckFixedValues(
        ConversionHeader header, Dictionary<long, Field> targetFields, HashSet<long> traceKeys, Dictionary<string, string[]> errors)
    {
        if (header.Set is not { Count: > 0 } set) return;
        var copied = (header.Copy ?? []).Select(m => m.To).ToHashSet();
        var seen = new HashSet<long>();
        foreach (var v in set)
            if (v is null || !targetFields.TryGetValue(v.FieldId, out var field) || field.Type != FieldType.Enum
                || !field.Options.Any(o => o.Id == v.OptionId && o.Active) || !seen.Add(v.FieldId) || traceKeys.Contains(v.FieldId) || copied.Contains(v.FieldId))
            {
                errors.TryAdd("definition.header.set", [FixedValueMessage]);
                return;
            }
    }

    public const string FillTargetMessage ="追溯欄位與數量欄位不能是參照欄位帶值的目的欄位（帶值會蓋掉轉換填的值）";

    /// <summary>啟用中的參照欄位所設定的帶值（任何模式）的目的欄位。</summary>
    private static HashSet<long> FillTargets(Module m) => m.Blocks.SelectMany(b => b.Fields)
        .Where(f => f is { Type: FieldType.Reference, Status: FieldStatus.Active, Config.Fill: not null })
        .SelectMany(f => f.Config.Fill!.Map.Select(x => x.To)).ToHashSet();

    private static bool IsDocument(Module m) => m.Kind == ModuleKind.Standalone && !ModuleRelations.IsDetail(m);

    private static Dictionary<long, Field> ActiveFields(Module m) =>
        m.Blocks.SelectMany(b => b.Fields).Where(f => f.Status == FieldStatus.Active).ToDictionary(f => f.Id);
}
