using System.Globalization;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;

namespace DynamicModules.Application.Dynamic.DemoService.Scenarios;

/// <summary>
/// 品質異常 30 筆（今天前 1–60 天均勻分布）：第 3、6…30 筆由 floor01、floor02 輪流建立（不選重大、不填不良率），
/// 其他 20 筆由 qa01 建立（其中 6 筆重大）；qa01 在 +1 天處置、+5 天結案。文中「第 n 筆」從 1 起算。
/// </summary>
public sealed class QualityScenario : IDemoScenario
{
    public const int Count = 30;

    public string Key => "quality";
    public int Seed => 20261104;

    private const string Qa = "qa01";
    private const int CriticalCount = 6;

    /// <summary>重大且未結案（距今 20 天內）至少要有的筆數。</summary>
    private const int CriticalOpenCount = 2;

    private static readonly Dictionary<string, string[]> Causes = new()
    {
        ["appearance"] = ["scratch", "stain", "burr"],
        ["dimension"] = ["oversize", "undersize", "warp"],
        ["function"] = ["no-power", "short"]
    };

    private static readonly string[] Kinds = ["appearance", "dimension", "function"];

    private static readonly string[] Actions = ["rework", "scrap", "sort", "concession", "return"];

    private static readonly string[] Units = ["成型課", "模具課", "組裝課", "外包廠商"];

    public void Plan(DemoPlan plan)
    {
        var today = plan.Today;
        var rnd = plan.Random;
        var orders = plan.Records.GetValueOrDefault("order") ?? [];
        var dates = Enumerable.Range(1, Count)
            .ToDictionary(n => n, n => today.AddDays(-(60 - (int)Math.Round((n - 1) * 59.0 / (Count - 1)))));
        var byQa = dates.Keys.Where(n => n % 3 != 0).ToList();
        // 6 筆重大：先從距今 20 天內的挑 2 筆（未結案），其餘從剩下的 qa01 記錄挑
        var recent = byQa.Where(n => today.DayNumber - dates[n].DayNumber <= 20).OrderBy(_ => rnd.Next()).Take(CriticalOpenCount).ToList();
        var critical = recent.Concat(byQa.Except(recent).OrderBy(_ => rnd.Next()).Take(CriticalCount - CriticalOpenCount)).ToHashSet();

        for (var n = 1; n <= Count; n++)
        {
            var date = dates[n];
            var days = today.DayNumber - date.DayNumber;
            var creator = n % 3 != 0 ? Qa : n / 3 % 2 == 1 ? "floor01" : "floor02";
            var candidates = orders.Where(o => o.Date <= date.AddDays(-1) && o.Facts["status"] is not ("draft" or "cancel")).ToList();
            var order = candidates[rnd.Next(candidates.Count)];
            var kind = Kinds[rnd.Next(Kinds.Length)];
            var cause = Causes[kind][rnd.Next(Causes[kind].Length)];
            var severity = critical.Contains(n) ? "critical" : rnd.Next(2) == 0 ? "minor" : "normal";
            var status = days switch { > 20 => "closed", >= 7 => "handling", _ => "open" };
            var label = $"品質異常第 {n} 筆";

            var createdAt = plan.At(date);
            var issue = plan.Create("quality", createdAt, creator, $"{label} 開立", ctx => new CreateRecordRequest(
                ctx.Ids.ModuleKeys["quality"],
                ctx.Values("quality",
                    ("order", order.Id), ("reportDate", Iso(date)), ("kind", ctx.Opt("quality", kind)),
                    ("cause", ctx.Opt("quality", cause)), ("severity", ctx.Opt("quality", severity)),
                    ("status", ctx.Opt("quality", "open")))));
            // 訂單的品質異常筆數重算會換訂單的 RowVersion，訂單後面還有推進步驟
            plan.Refresh(order, createdAt, "admin", $"{label} 重讀訂單");

            if (status == "open") continue;
            var actions = Actions.OrderBy(_ => rnd.Next()).Take(rnd.Next(1, 3)).ToArray();
            var rate = Math.Round(0.5m + (decimal)rnd.NextDouble() * 11.5m, 1);
            var unit = Units[rnd.Next(Units.Length)];
            var handlingAt = plan.At(date.AddDays(1), createdAt);
            plan.Update(issue, handlingAt, Qa, $"{label} 處置中", (ctx, r) => new UpdateRecordRequest(r.Id,
                ctx.Values("quality",
                    ("status", ctx.Opt("quality", "handling")), ("actions", ctx.Opts("quality", actions)),
                    ("defectRate", rate), ("responsibleUnit", unit)), r.RowVersion));
            if (status == "handling") continue;

            plan.Update(issue, plan.At(date.AddDays(5), handlingAt), Qa, $"{label} 結案", (ctx, r) => new UpdateRecordRequest(r.Id,
                ctx.Values("quality", ("status", ctx.Opt("quality", "closed"))), r.RowVersion));
        }
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
