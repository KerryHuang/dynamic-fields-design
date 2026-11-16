using DynamicModules.Application.Common;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Application.Dynamic.RecordService;

namespace DynamicModules.Application.Dynamic.PrintService;

/// <summary>範本內容的驗證與淨化。回傳的 Body 是淨化後要存的版本。</summary>
public class PrintTemplateValidator(IModuleRepository modules)
{
    public async Task<(ServiceResult? Invalid, string Body)> ValidateAsync(Module module, PrintTemplateInput input, bool isNew, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (isNew && !PrintTemplateRules.KeyPattern().IsMatch(input.Key ?? ""))
            errors["key"] = ["代碼格式錯誤（小寫英文、數字、連字號，2 到 40 字，英文開頭）"];
        if (input.Name is null || input.Name.Values.Values.All(string.IsNullOrWhiteSpace) || input.Name.Values.Values.Any(v => v is { Length: > 100 }))
            errors["name"] = ["名稱必填，每個語系最多 100 字"];
        if (string.IsNullOrWhiteSpace(input.Body))
            errors["body"] = ["範本內容必填"];
        else if (input.Body.Length > PrintTemplateRules.MaxBodyLength)
            errors["body"] = [$"範本內容最多 {PrintTemplateRules.MaxBodyLength} 字"];
        if (errors.Count > 0) return (ServiceResult.Invalid(errors), "");

        var body = PrintTemplateSanitizer.Sanitize(input.Body);
        var placeholders = PrintTemplateEngine.Placeholders(body);
        var problems = new List<string>();
        if (placeholders.Error is not null) problems.Add(placeholders.Error);
        if (PrintTemplateEngine.HasLoopMarkerInsideTag(body)) problems.Add(PrintTemplateEngine.LoopMarkerInTagMessage);

        var fieldKeys = module.Blocks.SelectMany(b => b.Fields).Select(f => f.Key).ToHashSet();
        problems.AddRange(placeholders.Header.Where(k => !fieldKeys.Contains(k)).Select(k => $"找不到欄位 {k}"));
        if (placeholders.Loops.Count > 0)
        {
            var details = await DetailWriterAccess.DetailModulesAsync(modules, module, ct);
            foreach (var (moduleKey, keys) in placeholders.Loops)
            {
                var child = details.FirstOrDefault(d => d.Key == moduleKey);
                if (child is null) { problems.Add($"{moduleKey} 不是這個模組的明細模組"); continue; }
                var childKeys = child.Blocks.SelectMany(b => b.Fields).Select(f => f.Key).ToHashSet();
                problems.AddRange(keys.Where(k => !childKeys.Contains(k)).Select(k => $"明細模組 {moduleKey} 找不到欄位 {k}"));
            }
        }
        return problems.Count == 0
            ? (null, body)
            : (ServiceResult.Invalid(new Dictionary<string, string[]> { ["body"] = [.. problems.Distinct()] }), "");
    }
}

/// <summary>DetailWriter.ReadableDetailModulesAsync 的設計器版：列出所有明細模組（不看啟用狀態）。</summary>
internal static class DetailWriterAccess
{
    public static async Task<List<Module>> DetailModulesAsync(IModuleRepository modules, Module parent, CancellationToken ct)
    {
        var result = new List<Module>();
        foreach (var (listed, _) in await modules.ListDetailModulesAsync(parent.Key, ct))
            result.Add((await modules.GetByIdAsync(listed.Id, includeChildren: true, ct))!);
        return result;
    }
}
