using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.FieldService.Data;
using DynamicModules.Application.Dynamic.ImportService.Data;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ImportService;

/// <summary>一列的結果：Values 是 RecordCreator.ValidateAsync 處理後的值；Errors 的鍵是欄位 key 或 _record（還沒加 rows[] 前綴）。</summary>
public sealed record ImportRowPlan(int Line, Dictionary<string, JsonElement?> Values, Dictionary<string, string[]> Errors);

public sealed record ImportPlan(Module Module, List<ImportColumn> Columns, List<ImportRowPlan> Rows);

/// <summary>
/// 預覽與確認共用（決策 6）：檔案檢查、欄位對應、預查使用者與參照標題、逐列解析並走 POST records 的同一套驗證。不寫入任何東西。
/// </summary>
public class ImportPlanner(
    IModuleRepository modules, IRecordRepository records, IPermissionRepository permissions, IDynamicAccessGuard guard,
    ReferenceProviderRegistry providers, RecordCreator creator)
{
    public const int MaxBytes = 1024 * 1024;
    public const string FileKey = "file";
    public const string ComputedColumn = "系統計算的欄位";
    public const string ReadOnlyColumn = "唯讀欄位";
    public const string UnknownColumn = "找不到這個欄位";
    public const string AmbiguousColumn = "對到多個欄位，請改用欄位代碼";
    private const int LookupPageSize = 100;

    /// <returns>Plan 為 null 時 Failure 是要直接回的結果（404、403、檔案層級的 400）。</returns>
    public async Task<(ImportPlan? Plan, ServiceResult? Failure)> PlanAsync(string moduleKey, byte[] content, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(moduleKey, includeChildren: true, ct);
        if (module is null || ModuleRelations.IsDetail(module))
            return (null, ServiceResult.NotFound(RecordMapper.ModuleNotFound));
        if (await ModuleGate.RejectUnwritableAsync(module, guard, ct) is { } blocked) return (null, blocked);
        var access = await guard.AuthorizeAsync(module, PermissionAction.Add, ct);
        if (access.Permissions is not { } perms) return (null, access.Denied);
        if (module.Kind == ModuleKind.Extension) return FileError("擴充模組不支援匯入");
        if (content.Length == 0) return FileError("請選擇檔案");
        if (content.Length > MaxBytes) return FileError("檔案不能超過 1 MB");
        var csv = CsvReader.Read(content);
        if (csv.Error is { } error) return FileError(error);

        // 只比對讀者看得到的啟用欄位：隱藏欄位當作不存在，不洩漏
        var fields = module.Blocks.OrderBy(b => b.Sequence).ThenBy(b => b.Id)
            .SelectMany(b => b.Fields.OrderBy(f => f.Sequence).ThenBy(f => f.Id))
            .Where(f => f.Status == FieldStatus.Active && perms.CanSee(f)).ToList();
        var columns = new List<ImportColumn>();
        var mapped = new List<(int Index, Field Field)>();
        var seen = new HashSet<long>();
        for (var i = 0; i < csv.Header.Count; i++)
        {
            var header = csv.Header[i].Trim();
            // 欄位代碼優先；標籤（任一語系）對到不只一個欄位時不猜，整欄略過
            var byLabel = fields
                .Where(f => f.LabelI18n.Values.Values.Any(l => l.Trim().Equals(header, StringComparison.OrdinalIgnoreCase)))
                .Take(2).ToList();
            var field = fields.FirstOrDefault(f => f.Key == header) ?? (byLabel.Count == 1 ? byLabel[0] : null);
            if (field is null && byLabel.Count > 1)
            {
                columns.Add(new ImportColumn(header, null, AmbiguousColumn));
                continue;
            }
            if (field is null)
            {
                columns.Add(new ImportColumn(header, null, UnknownColumn));
                continue;
            }
            if (!seen.Add(field.Id)) return FileError($"欄位「{field.LabelI18n.Resolve(I18nText.FallbackLocale)}」重複");
            if (FieldRules.IsComputed(field.Type)) columns.Add(new ImportColumn(header, null, ComputedColumn));
            else if (!perms.CanWrite(field) || field.Config.ReadOnly) columns.Add(new ImportColumn(header, null, ReadOnlyColumn));
            else
            {
                columns.Add(new ImportColumn(header, field.Key, null));
                mapped.Add((i, field));
            }
        }

        var lookups = new ImportLookups(await UsersAsync(ct), await ReferenceTitlesAsync(mapped, csv.Rows, ct));
        var rows = new List<ImportRowPlan>();
        foreach (var row in csv.Rows)
        {
            var input = new Dictionary<string, JsonElement?>();
            var parseErrors = new Dictionary<string, string[]>();
            foreach (var (index, field) in mapped)
            {
                // 空白格視同沒填：不送這個鍵，和 POST records 沒帶欄位一樣套預設值、必填照常檢查
                var text = Cell(row, index);
                if (text.Trim().Length == 0) continue;
                var (value, parseError) = FieldImportParser.Parse(field, text, lookups);
                if (parseError is not null) parseErrors[field.Key] = [parseError];
                else input[field.Key] = value;
            }
            var (values, errors) = await creator.ValidateAsync(module, perms, input, null, ct);
            // 解析失敗的欄位沒有送進驗證，以解析錯誤為準
            foreach (var (key, messages) in parseErrors) errors[key] = messages;
            rows.Add(new ImportRowPlan(row.Line, values, errors));
        }
        return (new ImportPlan(module, columns, rows), null);
    }

    private static (ImportPlan?, ServiceResult?) FileError(string message) =>
        (null, ServiceResult.Invalid(new Dictionary<string, string[]> { [FileKey] = [message] }));

    /// <summary>少於表頭欄數的列，缺的欄視為空字串。</summary>
    private static string Cell(CsvRow row, int index) => index < row.Cells.Count ? row.Cells[index] : "";

    /// <summary>
    /// 只收啟用中的帳號（停用者不能被新指派）。UserId 對自己一定收；DisplayName 只收唯一的——兩人同名時用名字對應不明確，
    /// 該名字查不到（回「找不到使用者」），改填 UserId 即可。
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> UsersAsync(CancellationToken ct)
    {
        var users = (await permissions.ListUsersAsync(ct)).Where(u => u.IsActive).ToList();
        var map = users.ToDictionary(u => u.UserId, u => u.UserId);
        foreach (var g in users.GroupBy(u => u.DisplayName).Where(g => g.Count() == 1))
            map.TryAdd(g.Key, g.Single().UserId);
        return map;
    }

    /// <summary>每個參照欄位只查一次：這一欄出現過的相異標題 → 標題完全相等的鍵。</summary>
    private async Task<IReadOnlyDictionary<long, IReadOnlyDictionary<string, IReadOnlyList<string>>>> ReferenceTitlesAsync(
        List<(int Index, Field Field)> mapped, IReadOnlyList<CsvRow> rows, CancellationToken ct)
    {
        var result = new Dictionary<long, IReadOnlyDictionary<string, IReadOnlyList<string>>>();
        foreach (var (index, field) in mapped.Where(m => m.Field is { Type: FieldType.Reference, Config.RefTarget: not null }))
        {
            var titles = rows.Select(r => FieldImportParser.StripInjectionApostrophe(Cell(r, index).Trim()))
                .Where(t => t.Length > 0).Distinct().ToList();
            result[field.Id] = titles.Count == 0
                ? new Dictionary<string, IReadOnlyList<string>>()
                : await TitleKeysAsync(field.Config.RefTarget!, titles, ct);
        }
        return result;
    }

    private async Task<Dictionary<string, IReadOnlyList<string>>> TitleKeysAsync(
        ReferenceTarget target, List<string> titles, CancellationToken ct)
    {
        var map = new Dictionary<string, IReadOnlyList<string>>();
        if (target.Kind == ReferenceTarget.EntityKind)
        {
            if (providers.Get(target.Key) is not { } provider) return map;
            foreach (var title in titles)
            {
                var keys = await EntityKeysAsync(provider, title, title, ct);
                // 實體標題常是「編號 名稱」，provider 的關鍵字只比對單一欄位：整串查不到時改用第一段再比一次標題
                if (keys.Count == 0 && title.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var first, _, ..])
                    keys = await EntityKeysAsync(provider, title, first, ct);
                map[title] = keys;
            }
            return map;
        }

        // 讀者對目標模組沒有 Read 或看不到標題：每個標題都當作找不到，不洩漏資料
        var targetModule = await modules.GetByKeyAsync(target.Key, includeChildren: true, ct);
        if (!targetModule.IsWritable()) return map;
        var access = await guard.AuthorizeAsync(targetModule, PermissionAction.Read, ct);
        if (access.Permissions is not { } perms || !targetModule.TitleVisibleTo(perms)) return map;
        foreach (var title in titles)
        {
            var keys = new List<string>();
            for (var page = 1; ; page++)
            {
                var (items, total) = await records.QueryLightAsync(targetModule.Id, title, perms.OwnerUserIds, page, LookupPageSize, ct);
                keys.AddRange(items.Where(i => i.Title == title).Select(i => i.Id.ToString()));
                if (items.Count == 0 || page * LookupPageSize >= total) break;
            }
            map[title] = keys;
        }
        return map;
    }

    private static async Task<List<string>> EntityKeysAsync(IReferenceProvider provider, string title, string keyword, CancellationToken ct)
    {
        var keys = new List<string>();
        for (var page = 1; ; page++)
        {
            var result = await provider.LightAsync(keyword, page, LookupPageSize, ct);
            keys.AddRange(result.Items.Where(i => i.Title == title).Select(i => i.Key));
            if (result.Items.Count == 0 || page * LookupPageSize >= result.Total) break;
        }
        return keys;
    }
}
