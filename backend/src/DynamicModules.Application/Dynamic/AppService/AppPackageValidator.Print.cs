using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.PrintService;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using static DynamicModules.Application.Dynamic.TemplateService.TemplateGraph;

namespace DynamicModules.Application.Dynamic.AppService;

// 格式 3 的列印範本：模組、代號、內文大小與佔位符引用。內文不在這裡淨化——開啟時走設計器指令，存進去的一律是淨化後的版本。
public static partial class AppPackageValidator
{
    private static void PrintTemplateReferences(AppPackageData p, Dictionary<string, TemplateField> fields, List<string> errors)
    {
        var moduleCodes = p.Modules.Select(m => m.Code).ToHashSet();
        var seen = new HashSet<(string, string)>();
        for (var i = 0; i < p.PrintTemplates.Count; i++)
        {
            var t = p.PrintTemplates[i];
            var at = $"printTemplates[{i}]";
            if (t is null) { errors.Add($"{at}: 不能是 null"); continue; }
            if (t.Module is null || !moduleCodes.Contains(t.Module)) { errors.Add($"{at}.module: 模組 {t.Module} 不在套件內"); continue; }
            if (!PrintTemplateRules.KeyPattern().IsMatch(t.Code ?? "")) errors.Add($"{at}.code: 格式錯誤（小寫英文、數字、連字號，2 到 40 字，英文開頭）");
            else if (!seen.Add((t.Module, t.Code!))) errors.Add($"{at}.code: 模組 {t.Module} 已有代號 {t.Code}");
            CheckText(t.Name, $"{at}.name", errors);
            if (string.IsNullOrWhiteSpace(t.Body)) { errors.Add($"{at}.body: 必填"); continue; }
            if (t.Body.Length > PrintTemplateRules.MaxBodyLength) { errors.Add($"{at}.body: 最多 {PrintTemplateRules.MaxBodyLength} 字"); continue; }

            var placeholders = PrintTemplateEngine.Placeholders(PrintTemplateSanitizer.Sanitize(t.Body));
            if (placeholders.Error is not null) errors.Add($"{at}.body: {placeholders.Error}");
            foreach (var key in placeholders.Header.Where(k => !fields.ContainsKey(Qualify(t.Module, k))))
                errors.Add($"{at}.body: 找不到欄位 {t.Module}.{key}");
            foreach (var (loop, keys) in placeholders.Loops)
            {
                if (!IsDetailOf(p, loop, t.Module)) { errors.Add($"{at}.body: {loop} 不是 {t.Module} 的明細模組"); continue; }
                foreach (var key in keys.Where(k => !fields.ContainsKey(Qualify(loop, k))))
                    errors.Add($"{at}.body: 找不到欄位 {loop}.{key}");
            }
        }
    }

    /// <summary>child 有一個 relation 為 detail、指向 parent 的參照欄位。</summary>
    private static bool IsDetailOf(AppPackageData p, string child, string parent) =>
        p.Modules.FirstOrDefault(m => m.Code == child) is { } module
        && module.Blocks.SelectMany(b => b.Fields).Any(f => f.Type == FieldType.Reference
            && ConfigString(f.Config, "relation") == "detail"
            && ConfigObject(f.Config, "refTarget") is { } target && ConfigString(target, "kind") == "module" && ConfigString(target, "app") is null
            && ConfigString(target, "key") == parent);
}
