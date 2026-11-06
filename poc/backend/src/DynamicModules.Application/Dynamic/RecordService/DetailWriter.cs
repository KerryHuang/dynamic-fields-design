using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Rollups;
using DynamicModules.Domain.Rules;

namespace DynamicModules.Application.Dynamic.RecordService;

/// <summary>明細列的一筆變更。Before 為 null 是新增（Record 在寫入後才有值）；After 為 null 是刪除。</summary>
public sealed class DetailRowChange(Record? record, IReadOnlyDictionary<string, JsonElement?>? before, Dictionary<string, JsonElement?>? after)
{
    public Record? Record { get; internal set; } = record;
    public IReadOnlyDictionary<string, JsonElement?>? Before { get; } = before;
    public Dictionary<string, JsonElement?>? After { get; } = after;
}

/// <summary>一個啟用中的明細模組：Existing 是寫入前資料庫裡這張表頭的未刪除列，Changes 是這次請求的變更（依請求順序）。</summary>
public sealed record DetailSet(
    Module Child, Field ParentField, EffectivePermissions Permissions, IReadOnlyList<Record> Existing, List<DetailRowChange> Changes);

public sealed class DetailPlan
{
    /// <summary>整個請求要直接回的結果（403、409）；有值時其他屬性不完整。</summary>
    public ServiceResult? Failure { get; init; }
    /// <summary>已加上 details.{childKey}[{i}]. 前綴的錯誤。</summary>
    public Dictionary<string, string[]> Errors { get; } = [];
    /// <summary>表頭上來源是明細欄位的彙總欄位，依這次存檔後的明細算出；null 表示沒有值。</summary>
    public Dictionary<string, JsonElement?> RollupValues { get; } = [];
    public List<DetailSet> Sets { get; } = [];
}

/// <summary>表頭存檔裡的明細（已定決策 3、4）：Prepare 收集錯誤並算出彙總，不寫入任何東西；Apply 在表頭有 Id 之後寫入。</summary>
public class DetailWriter(
    IModuleRepository modules, IRecordRepository records, INumberSequenceRepository sequences, FieldTypeRegistry registry,
    IDynamicAccessGuard guard, ReferenceValidator refs, ICurrentUser user, IBusinessTime time, RecordHistoryWriter history)
{
    public const string UnknownDetail = "明細模組不存在或未啟用";
    public const string RowNotFound = "明細不存在";
    public const string TooManyRows = "明細最多 200 列";
    public const int MaxRows = 200;

    /// <param name="parentRecord">null 表示新增表頭（沒有既有列）。</param>
    /// <param name="headerValues">算出表頭這次存檔後的值（ParentDefault 用）；只在新增的列需要時才呼叫，最多一次。</param>
    /// <param name="action">新增表頭用 Add、編輯表頭用 Edit；明細列的權限沿用表頭的這個動作。</param>
    public async Task<DetailPlan> PrepareAsync(
        Module parent, Record? parentRecord, PermissionAction action,
        IReadOnlyDictionary<string, List<DetailRowInput>>? details, CancellationToken ct,
        Func<Task<IReadOnlyDictionary<string, JsonElement?>>>? headerValues = null)
    {
        var plan = new DetailPlan();
        // 寫入只認 Active 的明細模組；Closed 的明細（不會與 Active 表頭並存）一律當成不存在
        var children = (await ReadableDetailModulesAsync(modules, parent, ct)).Where(c => c.IsWritable()).ToList();
        foreach (var key in (details?.Keys ?? Enumerable.Empty<string>()).Where(k => children.All(c => c.Key != k)))
            plan.Errors[$"details.{key}"] = [UnknownDetail];

        var userIds = await modules.ListUserIdsAsync(ct);
        IReadOnlyDictionary<string, JsonElement?>? header = null;
        async Task<IReadOnlyDictionary<string, JsonElement?>> HeaderOnceAsync()
        {
            if (header is not null) return header;
            var active = parent.Blocks.SelectMany(b => b.Fields).Where(f => f.Status == FieldStatus.Active).Select(f => f.Key).ToHashSet();
            return header = (await headerValues!()).Where(kv => active.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        }
        foreach (var child in children)
        {
            var access = await guard.AuthorizeDetailAsync(parent, child, action, ct);
            if (access.Permissions is not { } perms) return new DetailPlan { Failure = access.Denied };
            var parentField = ModuleRelations.DetailFieldOf(child)!;
            var existing = parentRecord is null ? [] : await records.ListByRefAsync(parentField.Id, parentRecord.Id.ToString(), ct);
            var set = new DetailSet(child, parentField, perms, existing, []);
            var needsHeader = child.Blocks.SelectMany(b => b.Fields).Any(f => f.Config.ParentDefaultFieldId is not null);

            var rows = details?.GetValueOrDefault(child.Key) ?? [];
            for (var i = 0; i < rows.Count; i++)
                if (await PrepareRowAsync(set, rows[i], $"details.{child.Key}[{i}].", parentRecord, userIds, plan.Errors, ct,
                        needsHeader && rows[i].Id is null && headerValues is not null ? await HeaderOnceAsync() : null))
                    return new DetailPlan { Failure = ServiceResult.Conflict(RecordMapper.Stale) };

            var after = existing.Count + set.Changes.Count(c => c.Before is null) - set.Changes.Count(c => c.After is null);
            if (after > MaxRows) plan.Errors[$"details.{child.Key}"] = [TooManyRows];
            ComputeRollups(parent, set, userIds, plan.RollupValues);
            plan.Sets.Add(set);
        }
        return plan;
    }

    /// <summary>以 parent 為表頭、可查閱（Active 或 Closed）的明細模組（含區塊、欄位、規則），依 Key 排序。</summary>
    internal static async Task<List<Module>> ReadableDetailModulesAsync(IModuleRepository modules, Module parent, CancellationToken ct)
    {
        var result = new List<Module>();
        foreach (var (listed, _) in await modules.ListDetailModulesAsync(parent.Key, ct))
            if (listed.IsReadable())
                result.Add((await modules.GetByIdAsync(listed.Id, includeChildren: true, ct))!);
        return result.OrderBy(m => m.Key, StringComparer.Ordinal).ToList();
    }

    /// <returns>true 表示列的 rowVersion 不符，整個請求要回 409。</returns>
    private async Task<bool> PrepareRowAsync(
        DetailSet set, DetailRowInput row, string prefix, Record? parentRecord, IReadOnlySet<string> userIds,
        Dictionary<string, string[]> errors, CancellationToken ct, IReadOnlyDictionary<string, JsonElement?>? parentValues = null)
    {
        Record? record = null;
        Dictionary<string, JsonElement?>? previous = null;
        if (row.Id is { } id)
        {
            record = set.Existing.FirstOrDefault(r => r.Id == id);
            if (record is null || set.Changes.Any(c => c.Record == record))
            {
                errors[prefix + RecordValueProcessor.RecordErrorKey] = [RowNotFound];
                return false;
            }
            if (record.RowVersion != row.RowVersion) return true;
            previous = RecordValueProcessor.Parse(record.ValuesJson);
            if (row.Delete)
            {
                // 明細列自己的全單唯讀只鎖這一列，刪除也擋
                if (RuleEngine.Evaluate(set.Child, previous, previous, registry, userIds, time.Today).ReadOnlyAll)
                    errors[prefix + RecordValueProcessor.RecordErrorKey] = [RecordValueProcessor.ReadOnlyRecordMessage];
                else set.Changes.Add(new DetailRowChange(record, previous, null));
                return false;
            }
        }
        else if (row.Delete) return false; // 還沒存過的列又被刪掉：不做任何事

        // 表頭欄位一律由伺服器填入，請求帶的值忽略
        var parentKey = set.ParentField.Key;
        var input = (row.Values ?? []).Where(kv => kv.Key != parentKey).ToDictionary(kv => kv.Key, kv => kv.Value);
        var validRefKeys = new Dictionary<long, IReadOnlySet<string>>(await refs.ValidKeysAsync(set.Child, input, previous, ct));
        if (previous is null)
        {
            // 新增表頭時還沒有 Id：先用 0 通過必填與參照檢查，寫入時換成表頭的 Id
            var parentId = parentRecord?.Id ?? 0;
            input[parentKey] = JsonSerializer.SerializeToElement(parentId);
            validRefKeys[set.ParentField.Id] = new HashSet<string> { parentId.ToString() };
        }
        var (values, rowErrors) = RecordValueProcessor.Process(
            set.Child, input, previous, userIds, registry, set.Permissions, new RuleContext(time.Today), validRefKeys,
            parentValues: parentValues);
        foreach (var (key, messages) in rowErrors) errors[prefix + key] = messages;
        set.Changes.Add(new DetailRowChange(record, previous, values));
        return false;
    }

    /// <summary>資料庫裡沒被這次請求修改或刪除的列，加上這次新增與修改後的列（決策 4 第 4 步）。</summary>
    private void ComputeRollups(Module parent, DetailSet set, IReadOnlySet<string> userIds, Dictionary<string, JsonElement?> into)
    {
        var rollups = parent.Blocks.SelectMany(b => b.Fields)
            .Where(f => f is { Type: FieldType.Rollup, Status: FieldStatus.Active } && f.Config.Rollup?.SourceFieldId == set.ParentField.Id)
            .ToList();
        if (rollups.Count == 0) return;
        var touched = set.Changes.Where(c => c.Before is not null).Select(c => c.Record!.Id).ToHashSet();
        var rows = set.Existing.Where(r => !touched.Contains(r.Id))
            .Select(r => (IReadOnlyDictionary<string, JsonElement?>)RecordValueProcessor.Parse(r.ValuesJson))
            .Concat(set.Changes.Where(c => c.After is not null).Select(c => (IReadOnlyDictionary<string, JsonElement?>)c.After!))
            .ToList();
        foreach (var rollup in rollups)
            into[rollup.Key] = RollupCalculator.Compute(rollup, set.Child, rows, time.Today, userIds);
    }

    /// <summary>寫入明細列（決策 4 第 8 步）；parentRecord 必須已有 Id。呼叫端負責 CommitAsync。</summary>
    public async Task ApplyAsync(DetailPlan plan, Record parentRecord, CancellationToken ct)
    {
        var now = time.Now;
        foreach (var set in plan.Sets)
            foreach (var change in set.Changes)
            {
                if (change.After is not { } values)
                {
                    // 明細模組不能被參照，刪除列不需要第 5 階段的刪除規劃
                    change.Record!.DeleteFlag = true;
                    Touch(change.Record, now);
                    history.Deleted(change.Record);
                    continue;
                }
                values[set.ParentField.Key] = JsonSerializer.SerializeToElement(parentRecord.Id);
                var record = change.Record;
                if (record is null)
                {
                    await RecordAutoNumbers.AssignAsync(sequences, set.Child, values, now, parentRecord.Id, ct);
                    record = new Record
                    {
                        ModuleId = set.Child.Id,
                        OwnerUserId = parentRecord.OwnerUserId,
                        RowVersion = Guid.NewGuid().ToString()
                    };
                    record.StampCreated(user.UserId, now);
                }
                else Touch(record, now);
                record.Title = RecordValueProcessor.TitleOf(set.Child, values);
                record.ValuesJson = RecordValueProcessor.Serialize(values);
                await records.ReplaceIndexAsync(record, RecordIndexer.Build(set.Child, values, registry), ct);
                if (change.Record is null)
                {
                    records.Add(record);
                    history.Created(record);
                    change.Record = record;
                }
                else history.Updated(record, set.Child, change.Before, values);
            }
    }

    private void Touch(Record r, DateTime now)
    {
        r.RowVersion = Guid.NewGuid().ToString();
        r.StampModified(user.UserId, now);
    }
}
