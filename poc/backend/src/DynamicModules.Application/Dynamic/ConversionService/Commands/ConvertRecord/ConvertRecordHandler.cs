using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.ConversionService.Data;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Application.Dynamic.ConversionService.Commands.ConvertRecord;

/// <summary>
/// 單據轉換。併發安全（設計文件 §4.4）：先把來源表頭記錄的 RowVersion 換新值並立刻寫入（帶舊值的條件更新），
/// 取得這張來源的互斥權之後才算剩餘量、建立目標單據。兩個人同時轉同一張，後到的更新會失敗（409），不會超轉。
/// 目標單據本身走既有的新增流程（CreateRecordHandler），權限、驗證、自動編號、彙總、帶值、狀態機一律照舊。
/// </summary>
public class ConvertRecordHandler(
    IConversionRepository conversions, IModuleRepository modules, IRecordRepository records, IDynamicAccessGuard guard,
    IUnitOfWork uow, ConversionLedger ledger, ConversionValidator validator, IRequestHandler<CreateRecordRequest, ServiceResult> create)
    : IRequestHandler<ConvertRecordRequest, ServiceResult>
{
    public const string NotFound = "轉換定義不存在或已停用";
    public const string SourceStateMessage = "來源單據目前的狀態不能轉換";
    public const string AlreadyConverted = "這張單據已經轉換過了";
    public const string NeedLines = "至少要選一列明細來轉換";
    public const string LineNotOfSource = "這一列不屬於這張來源單據";
    public const string OverRemaining = "轉換數量超過剩餘量";
    public const string DefinitionInvalid = "轉換定義已失效，請聯絡管理者檢查設定";
    public const string StoredMismatch = "目標單據存下的追溯值或數量與轉換不一致，已取消轉換";

    public async Task<ServiceResult> Handle(ConvertRecordRequest request, CancellationToken ct)
    {
        var conversion = await conversions.GetByKeyAsync(request.ConversionKey, ct);
        if (conversion is not { Active: true }) return ServiceResult.NotFound(NotFound);
        var record = await records.GetAsync(request.SourceId, ct);
        if (record is null || record.ModuleId != conversion.SourceModuleId) return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        var source = await modules.GetByIdAsync(conversion.SourceModuleId, includeChildren: true, ct);
        var target = await modules.GetByIdAsync(conversion.TargetModuleId, includeChildren: true, ct);
        if (!source.IsReadable() || !target.IsReadable()) return ServiceResult.NotFound(RecordMapper.ModuleNotFound);
        // Closed（應用程式關閉）的來源或目標都不能轉：轉換會寫目標，也會動來源記錄的版本號，兩邊都違反「資料唯讀」
        if (await ModuleGate.RejectUnwritableAsync(source, guard, ct) is { } sourceRejected) return sourceRejected;
        if (await ModuleGate.RejectUnwritableAsync(target, guard, ct) is { } targetRejected) return targetRejected;

        var access = await guard.AuthorizeAsync(source, PermissionAction.Read, ct);
        if (access.Permissions is not { } perms) return access.Denied!;
        if (perms.OwnerUserIds is not null && !perms.OwnerUserIds.Contains(record.OwnerUserId))
            return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        if (request.RowVersion is not null && request.RowVersion != record.RowVersion) return ServiceResult.Conflict(RecordMapper.Stale);

        var def = conversion.Definition;
        var sourceValues = RecordValueProcessor.Parse(record.ValuesJson);
        if (def.SourceStates.Count > 0 && (source.Workflow?.StateOf(sourceValues) is not { } state || !def.SourceStates.Contains(state)))
            return Invalid(RecordValueProcessor.RecordErrorKey, SourceStateMessage);
        if (def.Lines is not null && request.Lines is not { Count: > 0 }) return Invalid("lines", NeedLines);

        // 先確認使用者有目標模組的新增權限，免得白白搶鎖
        var addAccess = await guard.AuthorizeAsync(target, PermissionAction.Add, ct);
        if (addAccess.Permissions is not { } addPerms) return addAccess.Denied!;

        // 取互斥權之前重驗定義：設計器存檔後追溯欄位可能被停用、或被帶值設定佔用，那樣轉出去的單據會丟掉追溯值、已轉量算不到而反覆超轉
        if (!await validator.IsDefinitionValidAsync(source, target, def, ct)) return ServiceResult.Conflict(DefinitionInvalid);

        // 取得互斥權：條件更新來源記錄的 RowVersion；之後的剩餘量檢查與建立都在這把鎖之下
        record.RowVersion = Guid.NewGuid().ToString();
        try
        {
            await uow.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ServiceResult.Conflict(RecordMapper.Stale);
        }

        var sourceFields = source.Blocks.SelectMany(b => b.Fields).ToDictionary(f => f.Id);
        var overrides = request.Values ?? [];
        var targetFields = target.Blocks.SelectMany(b => b.Fields).ToDictionary(f => f.Id);
        var (headerValues, systemKeys) = CopyValues(def.Header.Copy, sourceFields, perms, targetFields, addPerms, sourceValues, overrides);
        var traceKey = $"f_{def.Header.SourceFieldId}";
        headerValues[traceKey] = JsonSerializer.SerializeToElement(record.Id);
        systemKeys.Add(traceKey);

        Dictionary<string, List<DetailRowInput>>? details = null;
        if (def.Lines is null)
        {
            if ((await ledger.TargetsOfAsync(conversion, record.Id, ct)).Count > 0) return ServiceResult.Conflict(AlreadyConverted);
        }
        else
        {
            var built = await BuildLinesAsync(conversion, source, target, record, request.Lines!, ct);
            if (built.Failure is not null) return built.Failure;
            details = new Dictionary<string, List<DetailRowInput>> { [def.Lines.TargetModuleKey] = built.Rows! };
        }

        var created = await create.Handle(new CreateRecordRequest(target.Key, headerValues, null, details, systemKeys), ct);
        if (!created.IsCreated) return created;
        return await VerifyStoredAsync(conversion, target, record.Id, (DynamicRecordResponse)created.Data!, details, ct) ? created : ServiceResult.Conflict(StoredMismatch);
    }

    /// <summary>
    /// 最後一道防線：目標表頭與每一列實際存下的追溯值、數量必須等於系統給的值（已轉量完全由它們算出來）。
    /// 讀資料庫的值，不讀回應——回應會依使用者的欄位權限遮蔽。回 false 時整筆交易回滾。
    /// </summary>
    private async Task<bool> VerifyStoredAsync(
        Conversion conversion, Module target, long sourceId, DynamicRecordResponse created,
        Dictionary<string, List<DetailRowInput>>? details, CancellationToken ct)
    {
        await uow.CommitAsync(ct);
        var def = conversion.Definition;
        var header = await records.GetAsync(created.Id, ct);
        if (header is null || ConversionLedger.NumberOf(RecordValueProcessor.Parse(header.ValuesJson), $"f_{def.Header.SourceFieldId}") != sourceId) return false;
        if (def.Lines is not { } lines) return true;

        var targetLine = await modules.GetByKeyAsync(lines.TargetModuleKey, includeChildren: true, ct);
        var stored = (await records.ListByRefAsync(ModuleRelations.DetailFieldOf(targetLine!)!.Id, header.Id.ToString(), ct))
            .Select(r => RecordValueProcessor.Parse(r.ValuesJson)).ToList();
        var (fromKey, qtyKey) = ($"f_{lines.SourceLineFieldId}", $"f_{lines.Quantity.To}");
        var expected = details![lines.TargetModuleKey]
            .Select(r => (ConversionLedger.NumberOf(r.Values!, fromKey), ConversionLedger.NumberOf(r.Values!, qtyKey))).OrderBy(x => x.Item1).ThenBy(x => x.Item2).ToList();
        var actual = stored.Select(v => (ConversionLedger.NumberOf(v, fromKey), ConversionLedger.NumberOf(v, qtyKey))).OrderBy(x => x.Item1).ThenBy(x => x.Item2).ToList();
        return expected.SequenceEqual(actual);
    }

    private async Task<(ServiceResult? Failure, List<DetailRowInput>? Rows)> BuildLinesAsync(
        Conversion conversion, Module source, Module target, Record sourceRecord, List<ConvertLineInput> requested, CancellationToken ct)
    {
        var lines = conversion.Definition.Lines!;
        var sourceLineModule = await modules.GetByKeyAsync(lines.SourceModuleKey, includeChildren: true, ct);
        var targetLineModule = await modules.GetByKeyAsync(lines.TargetModuleKey, includeChildren: true, ct);
        if (!sourceLineModule.IsWritable() || !targetLineModule.IsWritable())
            return (ServiceResult.NotFound(RecordMapper.ModuleNotFound), null);
        var lineAccess = await guard.AuthorizeDetailAsync(source, sourceLineModule, PermissionAction.Read, ct);
        if (lineAccess.Permissions is not { } linePerms) return (lineAccess.Denied, null);
        var targetLineAccess = await guard.AuthorizeDetailAsync(target, targetLineModule, PermissionAction.Add, ct);
        if (targetLineAccess.Permissions is not { } targetLinePerms) return (targetLineAccess.Denied, null);
        var targetLineFields = targetLineModule.Blocks.SelectMany(b => b.Fields).ToDictionary(f => f.Id);

        var parentField = ModuleRelations.DetailFieldOf(sourceLineModule)!;
        var fields = sourceLineModule.Blocks.SelectMany(b => b.Fields).ToDictionary(f => f.Id);
        var qtyField = fields[lines.Quantity.From];
        if (!linePerms.CanSee(qtyField)) return (ServiceResult.Forbidden(), null);

        var converted = await ledger.ConvertedAsync(conversion, targetLineModule, sourceRecord.Id, ct);
        var takenInRequest = new Dictionary<long, decimal>();
        var errors = new Dictionary<string, string[]>();
        var rows = new List<DetailRowInput>();
        for (var i = 0; i < requested.Count; i++)
        {
            var (lineId, quantity) = (requested[i].SourceLineId, requested[i].Quantity);
            var line = await records.GetAsync(lineId, ct);
            var values = line is null ? null : RecordValueProcessor.Parse(line.ValuesJson);
            if (line is null || line.ModuleId != sourceLineModule.Id
                || ConversionLedger.NumberOf(values!, parentField.Key) != sourceRecord.Id)
            {
                errors[$"lines[{i}].sourceLineId"] = [LineNotOfSource];
                continue;
            }
            var available = (ConversionLedger.NumberOf(values!, qtyField.Key) ?? 0)
                            - converted.GetValueOrDefault(lineId) - takenInRequest.GetValueOrDefault(lineId);
            if (quantity <= 0 || quantity > available)
            {
                errors[$"lines[{i}].quantity"] = [OverRemaining];
                continue;
            }
            takenInRequest[lineId] = takenInRequest.GetValueOrDefault(lineId) + quantity;

            var (rowValues, keys) = CopyValues(lines.Copy, fields, linePerms, targetLineFields, targetLinePerms, values!, new Dictionary<string, JsonElement?>());
            var qtyKey = $"f_{lines.Quantity.To}";
            var traceKey = $"f_{lines.SourceLineFieldId}";
            rowValues[qtyKey] = JsonSerializer.SerializeToElement(quantity);
            rowValues[traceKey] = JsonSerializer.SerializeToElement(lineId);
            keys.Add(qtyKey);
            keys.Add(traceKey);
            rows.Add(new DetailRowInput(null, rowValues, null, false, keys));
        }
        return errors.Count > 0 ? (ServiceResult.Invalid(errors), null) : (null, rows);
    }

    /// <summary>
    /// 依對應複製來源值（看不到的來源欄位不複製、使用者對目標寫不進去的目的欄位也不複製、沒有值的略過），再套上使用者給的覆寫值。
    /// 系統填入只略過「欄位設定為唯讀」這一層，權限層照舊：使用者不能寫（看不到或唯讀權限）的目的欄位不複製，
    /// 否則可先改來源、再轉換，把值寫進自己無權寫入的欄位。只有「沒被覆寫」的複製欄位才算系統填入；被覆寫的欄位照使用者自己的寫入權限處理。
    /// </summary>
    private static (Dictionary<string, JsonElement?> Values, HashSet<string> SystemKeys) CopyValues(
        List<DynamicModules.Domain.ValueObjects.FillMapping> copy, IReadOnlyDictionary<long, Field> sourceFields, EffectivePermissions perms,
        IReadOnlyDictionary<long, Field> targetFields, EffectivePermissions targetPerms,
        IReadOnlyDictionary<string, JsonElement?> sourceValues, IReadOnlyDictionary<string, JsonElement?> overrides)
    {
        var values = new Dictionary<string, JsonElement?>();
        var system = new HashSet<string>();
        foreach (var m in copy)
        {
            if (!sourceFields.TryGetValue(m.From, out var from) || !perms.CanSee(from)) continue;
            if (!targetFields.TryGetValue(m.To, out var to) || !targetPerms.CanWrite(to)) continue;
            if (!sourceValues.TryGetValue(from.Key, out var v) || v is not { ValueKind: not JsonValueKind.Null }) continue;
            values[$"f_{m.To}"] = v;
            system.Add($"f_{m.To}");
        }
        foreach (var (key, value) in overrides)
        {
            values[key] = value;
            system.Remove(key);
        }
        return (values, system);
    }

    private static ServiceResult Invalid(string key, string message) =>
        ServiceResult.Invalid(new Dictionary<string, string[]> { [key] = [message] });
}
