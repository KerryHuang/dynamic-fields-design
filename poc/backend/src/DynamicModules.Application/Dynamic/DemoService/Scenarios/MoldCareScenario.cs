using System.Globalization;
using System.Text.Json;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;

namespace DynamicModules.Application.Dynamic.DemoService.Scenarios;

/// <summary>
/// 今天前 180 天：maint01 建 12 支模具的擴充資料、manager01 建 8 家客戶的擴充資料。
/// 之後每支模具每隔一個保養週期一筆保養紀錄（含更換零件），每筆保養後 maint01 更新模具的上次保養日與累計射出數。
/// </summary>
public sealed class MoldCareScenario : IDemoScenario
{
    public string Key => "mold-care";
    public int Seed => 20261103;

    private const string Maint = "maint01";
    private const string Manager = "manager01";
    private const int HistoryDays = 180;

    /// <summary>第 n 支模具最後一筆保養的下次保養日 = 今天 + δ。</summary>
    private static readonly int[] Deltas = [-10, -3, 0, 2, 4, 6, 9, 15, 20, 25, 30, 40];

    /// <summary>
    /// 週期依序輪流。最後一筆保養日 = 今天 + δ − 週期，要 ≤ 昨天，所以週期必須大於 δ；
    /// 這個輪流順序剛好讓 δ 30、40 的模具分到 60、90 天，每支都滿足，不必另外調整。
    /// </summary>
    private static readonly int[] Cycles = [30, 45, 60, 90];

    private static readonly string[] Categories = ["clean", "parts", "calibrate", "lubricate", "polish"];

    private static readonly (string Name, int MinCost, int MaxCost)[] Parts =
    [
        ("頂針", 200, 800), ("彈簧", 50, 200), ("密封圈", 80, 300), ("導柱", 800, 3000), ("滑塊", 2000, 8000), ("水路接頭", 150, 600)
    ];

    private static readonly string[] Notes = ["分模面有輕微積碳，已清除", "水路流量偏低，下次保養複查", "頂出順暢，無異音"];

    private static readonly string[] PaymentTerms = ["月結 30 天", "月結 60 天", "貨到付款", "月結 90 天"];

    public void Plan(DemoPlan plan)
    {
        var today = plan.Today;
        var rnd = plan.Random;
        var start = today.AddDays(-HistoryDays);

        for (var c = 1; c <= 8; c++)
        {
            var code = $"C-{c:000}";
            var credit = new[] { "a", "b", "c" }[(c - 1) % 3];
            var rep = c % 2 == 1 ? "sales01" : "sales02";
            var terms = PaymentTerms[rnd.Next(PaymentTerms.Length)];
            var phone = $"04-23{rnd.Next(10, 100)}-{rnd.Next(1000, 10000)}";
            var email = $"purchase@c{c:000}.example.com";
            var site = $"https://c{c:000}.example.com";
            plan.Create("customer", plan.At(start), Manager, $"客戶資料 {code} 建立", ctx => new CreateRecordRequest(
                ctx.Ids.ModuleKeys["customer"],
                ctx.Values("customer",
                    ("creditLevel", ctx.Opt("customer", credit)), ("paymentTerms", terms), ("accountRep", rep),
                    ("email", email), ("phone", phone), ("website", site)),
                HostKey: code));
        }

        for (var m = 0; m < Deltas.Length; m++)
        {
            var code = $"M-{m + 1:0000}";
            var cycle = Cycles[m % Cycles.Length];
            var shots = rnd.Next(10_000, 200_001);
            var initialShots = shots;
            var createdAt = plan.At(start);
            var info = plan.Create("mold", createdAt, Maint, $"模具資料 {code} 建立", ctx => new CreateRecordRequest(
                ctx.Ids.ModuleKeys["mold"],
                ctx.Values("mold", ("cycleDays", cycle), ("shotCount", initialShots)),
                HostKey: code));

            // 最後一筆往前每隔一個週期一筆，不早於今天前 180 天；由舊到新規劃
            var dates = new List<DateOnly>();
            for (var d = today.AddDays(Deltas[m] - cycle); d >= start; d = d.AddDays(-cycle)) dates.Insert(0, d);

            var previous = createdAt;
            for (var k = 0; k < dates.Count; k++)
            {
                var at = plan.At(dates[k], previous);
                previous = at;
                var careDate = DateOnly.FromDateTime(at);
                var label = $"保養 {code} 第 {k + 1} 筆";
                var categories = Categories.OrderBy(_ => rnd.Next()).Take(rnd.Next(1, 4)).ToArray();
                var startTime = new TimeOnly(8, 0).AddMinutes(30 * rnd.Next(16)).ToString("HH:mm", CultureInfo.InvariantCulture);
                var cost = rnd.Next(5, 201) * 100;
                var note = rnd.NextDouble() < 0.3 ? Notes[rnd.Next(Notes.Length)] : null;

                var care = plan.Create("care", at, Maint, label, ctx =>
                {
                    var values = ctx.Values("care",
                        ("mold", code), ("categories", ctx.Opts("care", categories)), ("technician", Maint),
                        ("careDate", Iso(careDate)), ("startTime", startTime), ("cycleDays", cycle), ("cost", (decimal)cost));
                    if (note is not null) values[ctx.Key("care.note")] = JsonSerializer.SerializeToElement(note);
                    return new CreateRecordRequest(ctx.Ids.ModuleKeys["care"], values);
                });

                if (categories.Contains("parts"))
                {
                    var count = rnd.Next(1, 4);
                    for (var p = 0; p < count; p++)
                    {
                        var part = Parts[rnd.Next(Parts.Length)];
                        var qty = rnd.Next(1, 5);
                        var unitCost = rnd.Next(part.MinCost, part.MaxCost + 1);
                        plan.Create("part", at, Maint, $"{label} 更換零件 {p + 1}", ctx => new CreateRecordRequest(
                            ctx.Ids.ModuleKeys["part"],
                            ctx.Values("part", ("care", care.Id), ("partName", part.Name), ("qty", qty), ("unitCost", (decimal)unitCost))));
                    }
                }

                shots += rnd.Next(5_000, 30_001);
                var total = shots;
                plan.Update(info, at, Maint, $"{label} 更新模具資料", (ctx, r) => new UpdateRecordRequest(r.Id,
                    ctx.Values("mold", ("lastCareDate", Iso(careDate)), ("shotCount", total)), r.RowVersion));
            }
        }
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
