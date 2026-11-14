using System.Text.Json;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Reports;

namespace DynamicModules.Application.Dynamic.DemoService.Scenarios;

/// <summary>
/// 任務看板的示範資料（看板設計 §4）：業務與倉管人員的個人任務，給主管範圍（部屬的任務）示範。全部虛構。
/// 每個任務都由負責人自己建立（不產生「指派給你」通知）；到期日相對 plan.Today，建立與後續變更都在昨天上班時間內。
/// 「整理樣品櫃」建立後清空負責人，留下「未指派、建立者是部屬」的情況；「確認標準導柱庫存」掛在進銷存品號記錄上，
/// 業務主管讀不到那個模組，示範「相關記錄（無權限查看）」。
/// </summary>
public sealed class TaskBoardScenario : IDemoScenario
{
    public string Key => "task-board";
    public int Seed => 20261114;

    /// <summary>InventoryScenario 的品號清單裡「標準導柱」的位置。</summary>
    private const int GuidePostItem = 2;

    /// <param name="DaysFromToday">到期日相對今天的天數；null 表示無到期日。WeekEnd 表示本週最後一天。</param>
    private sealed record Spec(string Owner, string Title, int? DaysFromToday, string End, bool WeekEnd = false);

    private static readonly Spec[] Personal =
    [
        new("sales01", "整理嵐岳精密報價比較", -2, "inProgress"),
        new("sales01", "回覆恆川機械樣品問題", 0, "todo"),
        new("sales01", "準備週會業績摘要", null, "todo", WeekEnd: true),
        new("sales01", "更新客戶聯絡人資料", 12, "todo"),
        new("sales01", "整理名片", null, "todo"),
        new("sales01", "寄送曜陽電子型錄", -3, "done"),
        new("sales02", "確認岳峰精機交期", -1, "todo"),
        new("sales02", "季度目標草稿", null, "inProgress"),
        new("sales03", "整理展場報名資料", 0, "todo"),
        new("keeper01", "盤點備品倉", 1, "todo"),
    ];

    public void Plan(DemoPlan plan)
    {
        var today = plan.Today;
        var weekEnd = ReportDates.WeekStart(today, PolicyCatalog.WeekStartOf(PolicyCatalog.CalendarWeekStart.Factory)).AddDays(6);
        var yesterday = today.AddDays(-1);
        var clock = yesterday.ToDateTime(new TimeOnly(8, 30));
        DateTime Next() => clock = clock.AddMinutes(15);   // 昨天上班時間內依序排開（約 20 步，不會跨到今天）

        foreach (var spec in Personal)
        {
            var due = spec.WeekEnd ? weekEnd : spec.DaysFromToday is { } d ? today.AddDays(d) : (DateOnly?)null;
            var task = new Holder();
            plan.Run(Next(), spec.Owner, $"任務 {spec.Title} 建立", async (ctx, ct) =>
            {
                var r = await ctx.Sender.Send(new CreateTaskRequest(new TaskInput(spec.Title, null, spec.Owner, due)), ct);
                if (r.IsCreated) task.Id = ((TaskDetailResponse)r.Data!).Id;
                return r;
            });
            if (spec.End != "todo")
                plan.Run(Next(), spec.Owner, $"任務 {spec.Title} 改成 {spec.End}", (ctx, ct) =>
                    ctx.Sender.Send(new UpdateTaskRequest(task.Id, JsonSerializer.SerializeToElement(new { status = spec.End })), ct));
        }

        // 建立者是部屬、沒有負責人：建立並指派給自己，再清空負責人（未指派不通知）
        var cabinet = new Holder();
        plan.Run(Next(), "sales02", "任務 整理樣品櫃 建立", async (ctx, ct) =>
        {
            var r = await ctx.Sender.Send(new CreateTaskRequest(new TaskInput("整理樣品櫃", null, "sales02", today.AddDays(5))), ct);
            if (r.IsCreated) cabinet.Id = ((TaskDetailResponse)r.Data!).Id;
            return r;
        });
        plan.Run(Next(), "sales02", "任務 整理樣品櫃 清空負責人", (ctx, ct) =>
            ctx.Sender.Send(new UpdateTaskRequest(cabinet.Id, JsonSerializer.SerializeToElement(new { assigneeUserId = (string?)null })), ct));

        // 掛在進銷存品號記錄上（業務一在查詢群組裡讀得到、業務主管讀不到）
        if (plan.Records.TryGetValue("inv-item", out var items) && items.Count > GuidePostItem)
        {
            var item = items[GuidePostItem];
            plan.Run(Next(), "sales01", "任務 確認標準導柱庫存 建立", (ctx, ct) =>
                ctx.Sender.Send(new CreateRecordTaskRequest(item.Id,
                    new TaskInput("確認標準導柱庫存", null, "sales01", today.AddDays(2))), ct));
        }
    }

    /// <summary>步驟之間傳遞建立後才知道的任務 Id。</summary>
    private sealed class Holder
    {
        public long Id { get; set; }
    }
}
