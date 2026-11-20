using System.Text.Encodings.Web;
using System.Text.Json;
using DynamicModules.Application.Common.Localization;
using DynamicModules.ApplicationTests.Common;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using F = DynamicModules.Domain.Reports.TaskReportFields;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>任務報表的虛擬模組文字（語系包 i4c 計畫判斷 9、14）：搬表前後輸出逐字相同。</summary>
public class TaskReportTextTests
{
    private static I18nText Zh(string s) => new([KeyValuePair.Create("zh-TW", s)]);

    private static readonly Department[] Departments =
        [new() { Id = 5, NameI18n = Zh("業務一"), Active = true }, new() { Id = 6, NameI18n = Zh("舊部門"), Active = false }];
    private static readonly Module[] Modules = [new() { Id = 11, Key = "order", NameI18n = Zh("訂單") }, new() { Id = 12, Key = "leave", NameI18n = Zh("請假單") }];
    private static readonly TaskRule[] Rules = [new() { Id = 21, ModuleId = 11, NameI18n = Zh("出貨提醒") }];

    private static readonly string[] Datasets = [ReportDatasets.Tasks, ReportDatasets.TaskEvents, "foo"];

    private static string Path(string relative) =>
        System.IO.Path.Combine(BuiltinServerTextTests.RepoRoot(), "backend", "tests", "DynamicModules.ApplicationTests", relative);

    private static SortedDictionary<string, string> Raw(I18nText t) => new(t.Values.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal);

    /// <summary>快照形狀：每列（區段、資料來源、欄位代碼、選項 Id、Values 原值），依前四項排序。</summary>
    internal static string Snapshot(Func<string, Module> build, IReadOnlyDictionary<long, I18nText> blanks, IReadOnlyDictionary<string, I18nText> datasetNames)
    {
        var rows = new List<(string Section, string Dataset, long Field, long Option, SortedDictionary<string, string> Values)>();
        foreach (var d in Datasets)
        {
            var m = build(d);
            rows.Add(("module", d, 0, 0, Raw(m.NameI18n)));
            foreach (var f in m.Blocks.SelectMany(b => b.Fields))
            {
                rows.Add(("field", d, f.Id, 0, Raw(f.LabelI18n)));
                foreach (var o in f.Options) rows.Add(("option", d, f.Id, o.Id, Raw(o.LabelI18n)));
            }
        }
        foreach (var (id, t) in blanks) rows.Add(("blank", "", id, 0, Raw(t)));
        foreach (var (d, t) in datasetNames) rows.Add(("datasetName", d, 0, 0, Raw(t)));
        var ordered = rows.OrderBy(r => r.Section, StringComparer.Ordinal).ThenBy(r => r.Dataset, StringComparer.Ordinal)
            .ThenBy(r => r.Field).ThenBy(r => r.Option)
            .Select(r => new { section = r.Section, dataset = r.Dataset, field = r.Field, option = r.Option, values = r.Values });
        return JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
    }

    private static string Verified() => File.ReadAllText(Path("Dynamic/Tasks/task-report-texts.verified.json"));

    [Fact]
    public void BuiltinLabels_MatchSnapshot()
    {
        var m = BuiltinMessageCatalog.Instance;
        var actual = Snapshot(d => F.Build(d, Departments, Modules, Rules, k => m.AsI18n(k)),
            F.BlankLabelKeys.ToDictionary(kv => kv.Key, kv => m.AsI18n(kv.Value)),
            F.DatasetNameKeys.ToDictionary(kv => kv.Key, kv => m.AsI18n(kv.Value)));
        actual.Should().Be(Verified());
    }

    [Fact]
    public void EveryTextKey_InCatalog()
    {
        F.AllTextKeys.Should().HaveCount(41).And.OnlyHaveUniqueItems()
            .And.OnlyContain(k => k.StartsWith("report.Task", StringComparison.Ordinal) && BuiltinServerText.Table.ContainsKey(k));
        F.BlankLabelKeys.Values.Concat(F.DatasetNameKeys.Values).Should().BeSubsetOf(F.AllTextKeys);
    }

    [Fact]
    public async Task Ja_LabelsFollowChain()
    {
        var env = await MessageCatalogTests.CreateAsync("zh-TW", r => r.Add("ja", "日本語", fallback: "en-US"));
        var m = F.Build(ReportDatasets.Tasks, Departments, Modules, Rules, k => env.Catalog.AsI18n(k));
        var status = m.Blocks.SelectMany(b => b.Fields).Single(f => f.Id == F.Status);
        status.LabelI18n.Values.Should().ContainKey("ja");
        status.LabelI18n.Values["ja"].Should().Be("Status");
        m.NameI18n.Values["ja"].Should().Be("Task");
    }
}
