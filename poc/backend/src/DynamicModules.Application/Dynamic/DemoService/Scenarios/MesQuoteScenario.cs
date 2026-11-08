using System.Globalization;
using DynamicModules.Application.Dynamic.ConversionService.Commands.ConvertRecord;
using DynamicModules.Application.Dynamic.ConversionService.Data;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;

namespace DynamicModules.Application.Dynamic.DemoService.Scenarios;

/// <summary>
/// 「MES 模具廠」的報價到訂單：8 張報價單（今天前 12–70 天），業務建立並送審、業務主管核准或退回；
/// 已核准的報價單依 <c>mes-quote-to-order</c> 轉成訂單（一張全轉、一張分兩次轉完、一張只轉一部分），轉出的訂單由業務確認。
/// 全部走一般的建立、更新、轉換指令，權限、狀態機、帶值與逐項追蹤已轉數量照常生效。客戶用 Demo 的 C-001…C-008。
/// </summary>
public sealed class MesQuoteScenario : IDemoScenario
{
    public string Key => "mes-quote";
    public int Seed => 20261108;

    /// <summary>轉出的訂單放在這個代號底下（生產情境依它找訂單）。</summary>
    public const string OrderCode = "mes-quote-order";

    private const string Manager = "manager01";
    private static readonly string[] Reps = ["sales01", "sales02"];

    private static readonly (string No, string Name, int Price)[] Items =
    [
        ("GP-01", "導柱", 180), ("GB-02", "導套", 150), ("EP-03", "頂針", 60), ("MB-04", "模板", 2400), ("CR-05", "模仁", 5200), ("SL-06", "滑塊", 860)
    ];

    /// <param name="DaysAgo">建立日距今天數。</param>
    /// <param name="End">draft／pending／rejected／approved。</param>
    /// <param name="Conversions">已核准才有：每次轉單各行要轉的比例（百分比，0 表示該行這次不轉）。</param>
    private sealed record Spec(int DaysAgo, string End, int[][] Conversions);

    private static readonly Spec[] Specs =
    [
        new(70, "approved", [[100, 100]]),
        new(62, "approved", [[40, 40, 0], [60, 60, 100]]),
        new(55, "approved", [[50, 0]]),
        new(46, "approved", [[100]]),
        new(38, "rejected", []),
        new(30, "pending", []),
        new(20, "draft", []),
        new(12, "approved", [])
    ];

    public void Plan(DemoPlan plan)
    {
        var today = plan.Today;
        var rnd = plan.Random;
        DateTime After(DateTime t, int days = 1) => plan.At(DateOnly.FromDateTime(t).AddDays(days), t);

        for (var i = 0; i < Specs.Length; i++)
        {
            var spec = Specs[i];
            var n = i;
            var rep = Reps[i % Reps.Length];
            var date = today.AddDays(-spec.DaysAgo);
            var customer = $"C-{i + 1:000}";
            var lineCount = spec.Conversions.Length > 0 ? spec.Conversions.Max(c => c.Length) : rnd.Next(1, 4);
            var lines = Enumerable.Range(0, lineCount).Select(k =>
            {
                var item = Items[(i + k * 2) % Items.Length];
                return (item.No, item.Name, Qty: rnd.Next(2, 21) * 10, item.Price);
            }).ToList();
            var label = $"MES 報價單第 {i + 1} 張";

            var at = plan.At(date);
            var quote = plan.Create("mes-quote", at, rep, $"{label} 建立", ctx => new CreateRecordRequest(
                ctx.Ids.ModuleKeys["quote"],
                ctx.Values("quote", ("customer", customer), ("quoteDate", Iso(date)), ("validUntil", Iso(date.AddDays(30))),
                    ("status", ctx.Opt("quote", "draft")), ("owner", rep)),
                Details: new()
                {
                    [ctx.Ids.ModuleKeys["quote-line"]] = lines.Select(l => new DetailRowInput(null, ctx.Values("quote-line",
                        ("itemNo", l.No), ("itemName", l.Name), ("qty", (decimal)l.Qty), ("unit", "pcs"), ("price", (decimal)l.Price)), null)).ToList()
                }));
            if (spec.End == "draft") continue;

            at = After(at);
            plan.Update(quote, at, rep, $"{label} 送審", Status("pending"));
            if (spec.End == "pending") continue;

            at = After(at);
            plan.Update(quote, at, Manager, $"{label} " + (spec.End == "rejected" ? "退回" : "核准"), Status(spec.End));
            if (spec.End == "rejected") continue;

            for (var c = 0; c < spec.Conversions.Length; c++)
            {
                var percents = spec.Conversions[c];
                at = After(at, 2);
                plan.Refresh(quote, at, rep, $"{label} 轉單前重讀");
                at = After(at, 0);
                var order = plan.Convert(OrderCode, at, rep, $"{label} 第 {c + 1} 次轉訂單", ctx =>
                {
                    var rows = quote.DetailRows[ctx.Ids.ModuleKeys["quote-line"]];
                    var take = percents.Select((p, k) => (p, k)).Where(x => x.p > 0 && x.k < rows.Count)
                        .Select(x => new ConvertLineInput(rows[x.k].Id, Math.Max(1, lines[x.k].Qty * x.p / 100))).ToList();
                    return new ConvertRecordRequest(quote.Id, "mes-quote-to-order", null, null, take);
                });
                order.Facts["status"] = "confirmed";
                order.Facts["customer"] = customer;
                at = After(at);
                plan.Update(order, at, rep, $"{label} 轉出的訂單確認", (ctx, r) =>
                    new UpdateRecordRequest(r.Id, ctx.Values("order", ("status", ctx.Opt("order", "confirmed"))), r.RowVersion));
            }
        }
    }

    private static Func<DemoContext, DemoRecord, UpdateRecordRequest> Status(string code) =>
        (ctx, r) => new UpdateRecordRequest(r.Id, ctx.Values("quote", ("status", ctx.Opt("quote", code))), r.RowVersion);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
