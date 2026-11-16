using System.Globalization;
using System.Text.Json;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;

namespace DynamicModules.Application.Dynamic.DemoService.Scenarios;

/// <summary>
/// 故障報修 50 筆（今天前 1–90 天均勻分布）：floor01、floor02 輪流提報，第 10、20…50 筆由 maint01 提報；
/// maint01 處理，建立者結案。文中「第 n 筆」從 1 起算。
/// </summary>
public sealed class RepairScenario : IDemoScenario
{
    public const int Count = 50;

    public string Key => "repair";
    public int Seed => 20261102;

    private const string Maint = "maint01";

    /// <summary>relatedRepair 指向同模具較早一筆的報修（第 n 筆）。</summary>
    private static readonly int[] Related = [7, 23, 41];

    private static readonly string[] Categories = ["leak", "crack", "wear", "stuck"];

    private static readonly Dictionary<string, string[]> Descriptions = new()
    {
        ["leak"] = ["冷卻水路接頭滲水", "模仁周邊有水漬，疑似水路破裂"],
        ["crack"] = ["模仁角落出現細裂紋", "頂針孔周圍龜裂"],
        ["wear"] = ["分模面磨耗，產品毛邊變大", "滑塊磨耗，開模有異音"],
        ["stuck"] = ["開模時產品卡在公模", "頂出不順，滑塊卡住"],
        ["other"] = ["溫控器顯示異常", "模具定位環鬆脫"]
    };

    private static readonly string[] OtherNotes = ["周邊設備連動異常", "外部廠商反映", "原因待查"];

    private static readonly string[] Resolutions =
        ["更換密封圈並試模確認", "研磨修補後試模正常", "更換頂針與彈簧", "清理水路並更換接頭", "調整滑塊間隙後試模正常"];

    public void Plan(DemoPlan plan)
    {
        var today = plan.Today;
        var rnd = plan.Random;
        if (!plan.Records.TryGetValue("mold", out var molds) || molds.Count == 0)
            throw new InvalidOperationException("模具保養情境必須排在報修之前：報修要挑的模具資料還沒有規劃");
        var planned = new List<(int N, string Creator, DemoRecord Mold, DemoRecord Record)>();
        var toggle = false;

        for (var n = 1; n <= Count; n++)
        {
            var date = today.AddDays(-(90 - (int)Math.Round((n - 1) * 89.0 / (Count - 1))));
            var days = today.DayNumber - date.DayNumber;
            var creator = n % 10 == 0 ? Maint : n % 2 == 1 ? "floor01" : "floor02";
            var mold = molds[rnd.Next(molds.Count)];
            DemoRecord? related = null;
            if (Related.Contains(n))
            {
                // 建立者看得到的較早一筆（現場人員的範圍不含 maint01 建的），模具改成同一副
                var candidates = planned.Where(p => p.Creator != Maint).ToList();
                var target = candidates[rnd.Next(candidates.Count)];
                related = target.Record;
                mold = target.Mold;
            }
            var category = rnd.NextDouble() < 0.1 ? "other" : Categories[rnd.Next(Categories.Length)];
            var otherNote = category == "other" ? OtherNotes[rnd.Next(OtherNotes.Length)] : null;
            var urgent = rnd.NextDouble() < 0.2;
            var occurredAt = $"{Iso(date)}T07:{rnd.Next(60):00}:00+08:00";
            var description = Descriptions[category][rnd.Next(Descriptions[category].Length)];
            var status = days switch
            {
                > 14 => "closed",
                >= 5 => (toggle = !toggle) ? "verify" : "working",
                _ => "pending"
            };
            var label = $"報修第 {n} 筆";

            var createdAt = plan.At(date);
            var record = plan.Create("repair", createdAt, creator, $"{label} 提報", ctx =>
            {
                var values = ctx.Values("repair",
                    ("mold", mold.Id), ("requester", creator), ("reportDate", Iso(date)), ("occurredAt", occurredAt),
                    ("urgent", urgent), ("category", ctx.Opt("repair", category)),
                    ("status", ctx.Opt("repair", "pending")), ("description", description));
                if (otherNote is not null) values[ctx.Key("repair.otherNote")] = Json(otherNote);
                if (related is not null) values[ctx.Key("repair.relatedRepair")] = Json(related.Id);
                return new CreateRecordRequest(ctx.Ids.ModuleKeys["repair"], values);
            });
            record.Facts["status"] = status;
            planned.Add((n, creator, mold, record));

            if (status == "pending") continue;
            var workingAt = plan.At(date.AddDays(rnd.Next(2)), createdAt);
            plan.Update(record, workingAt, Maint, $"{label} 處理中", (ctx, r) => new UpdateRecordRequest(r.Id,
                ctx.Values("repair", ("status", ctx.Opt("repair", "working")), ("handler", Maint)), r.RowVersion));
            if (status == "working") continue;

            var resolution = Resolutions[rnd.Next(Resolutions.Length)];
            var finish = date.AddDays(rnd.Next(1, 6));
            if (finish >= today) finish = today.AddDays(-1);
            var downtime = Math.Round(0.5m + (decimal)rnd.NextDouble() * 15.5m, 1);
            var verifyAt = plan.At(date.AddDays(2), workingAt);
            plan.Update(record, verifyAt, Maint, $"{label} 待驗收", (ctx, r) => new UpdateRecordRequest(r.Id,
                ctx.Values("repair", ("status", ctx.Opt("repair", "verify")), ("resolution", resolution),
                    ("finishDate", Iso(finish)), ("downtime", downtime)), r.RowVersion));
            if (status == "verify") continue;

            plan.Update(record, plan.At(date.AddDays(3), verifyAt), creator, $"{label} 結案", (ctx, r) => new UpdateRecordRequest(r.Id,
                ctx.Values("repair", ("status", ctx.Opt("repair", "closed"))), r.RowVersion));
        }
    }

    private static JsonElement? Json(object value) => JsonSerializer.SerializeToElement(value);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
