using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>
/// 鐵則（設計 §14.5）：事件、檢查、分錄的核心程式不含任何業務名詞；情境只存在於測試與使用者的定義裡。
/// 詞表放在這裡，不放進被掃描的檔案。
/// </summary>
public class NoBusinessWordsTests
{
    private static readonly string[] ChineseWords =
        ["庫存", "工令", "傳票", "出貨", "報工", "請假", "倉庫", "訂單", "發票", "薪資", "進貨", "過帳", "借方", "貸方", "科目"];

    private static readonly string[] EnglishWords =
        ["stock", "inventory", "voucher", "shipment", "shipping", "payroll", "invoice", "warehouse", "salary", "debit", "credit"];

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "DynamicModules.Application"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到 poc/backend");
    }

    private static IEnumerable<string> Files()
    {
        var src = Path.Combine(Root(), "src");
        var app = Path.Combine(src, "DynamicModules.Application");
        var domain = Path.Combine(src, "DynamicModules.Domain");
        foreach (var dir in new[] { "EventService", "CheckService", "LedgerService" })
            foreach (var f in Directory.EnumerateFiles(Path.Combine(app, "Dynamic", dir), "*.cs", SearchOption.AllDirectories)) yield return f;
        foreach (var f in new[]
                 {
                     Path.Combine(domain, "Entities", "Dynamic", "ModuleEvent.cs"), Path.Combine(domain, "Entities", "Dynamic", "ModuleCheck.cs"),
                     Path.Combine(domain, "Entities", "Dynamic", "ModuleLedger.cs"), Path.Combine(app, "Common", "Behaviors", "RecordEffectsBehavior.cs"),
                     Path.Combine(app, "Common", "Security", "SystemIdentity.cs"), Path.Combine(app, "Dynamic", "Rollups", "FrozenRecords.cs"),
                     Path.Combine(app, "Dynamic", "AppService", "AppEffectsCodec.cs"), Path.Combine(app, "Dynamic", "AppService", "AppPackageValidator.V4.cs"),
                 })
            yield return f;
    }

    [Fact]
    public void Core_files_exist_so_the_scan_is_not_vacuous()
    {
        var files = Files().ToList();
        files.Should().OnlyContain(f => File.Exists(f));
        files.Count.Should().BeGreaterThan(20);
    }

    [Fact]
    public void Core_code_contains_no_business_words()
    {
        var hits = new List<string>();
        foreach (var file in Files())
        {
            var text = File.ReadAllText(file);
            foreach (var w in ChineseWords.Where(text.Contains)) hits.Add($"{Path.GetFileName(file)}: {w}");
            foreach (var w in EnglishWords.Where(w => System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{w}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                hits.Add($"{Path.GetFileName(file)}: {w}");
        }
        hits.Should().BeEmpty();
    }
}
