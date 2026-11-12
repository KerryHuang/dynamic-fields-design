using System.Globalization;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.AttachmentService;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.TaskService;

namespace DynamicModules.Application.Dynamic.DemoService.Scenarios;

/// <summary>
/// 「CRM 與銷售」的設計案：三筆，示範產品設計與簽核。全部走一般的建立、更新、上傳、留言、任務完成、送簽、同意指令。
/// <list type="bullet">
/// <item>感測器外殼改版：走完兩輪設計與客戶確認、送簽、同意、結案（看完整歷程）。</item>
/// <item>控制箱面板改版：停在第二輪的客戶確認中，「將設計圖交客戶確認」待辦在業務手上（現場示範送簽、同意或退回）。</item>
/// <item>展示架結構設計：停在第一輪設計中，設計人員有「繪製設計圖」待辦，還沒有圖。</item>
/// </list>
/// 設計圖是程式產生的虛構示意圖（<see cref="DemoDrawings"/>）；人名、案名、需求、客戶意見全是虛構。客戶取自共用主檔的 CRM 客戶。
/// </summary>
public sealed class DesignCaseScenario : IDemoScenario
{
    public const string CaseModule = "crm-design-case";
    public string Key => "design-case";
    public int Seed => 20261110;

    private const string Manager = "manager01", Sales = "sales01", Designer = "designer01";
    private const string IntakeTitle = "確認需求並交設計", DrawTitle = "依需求繪製／修改設計圖", ConfirmTitle = "將設計圖交客戶確認";

    public void Plan(DemoPlan plan)
    {
        var customers = MasterScenario.ForApp(plan, DemoCatalog.CrmAppId);
        if (customers.Count < 3) return;   // 沒開 CRM（或主檔沒建）時沒有客戶可掛

        // 第一筆：完整歷程
        var closed = Begin(plan, "感測器外殼改版", "外殼改成鋁合金，正面加指示燈窗", customers[0], -20, out var t);
        t = Intake(plan, closed, -19, t);
        t = FirstDrawing(plan, closed, "感測器外殼", -17, t);
        t = CustomerFeedback(plan, closed, customers[0], "感測器外殼", "指示燈窗改到右上角，側面加散熱槽", -15, t);
        t = SecondDrawing(plan, closed, "感測器外殼", -12, t, setCustomerReview: true);
        t = ConfirmSecondRound(plan, closed, "客戶確認此版", -10, t);
        var submit = plan.At(Day(plan, -10), t);
        plan.Refresh(closed, submit, Sales, "設計案 感測器外殼改版 重讀");
        var submitted = plan.At(Day(plan, -10), submit);
        plan.Run(submitted, Sales, "設計案 感測器外殼改版 送簽", (ctx, ct) => Submit(ctx, closed, "客戶已確認第二版", ct));
        var approve = plan.At(Day(plan, -9), submitted);
        plan.Run(approve, Manager, "設計案 感測器外殼改版 同意", (ctx, ct) => Approve(ctx, closed, "同意", ct));
        var close = plan.At(Day(plan, -8), approve);
        plan.Refresh(closed, close, Manager, "設計案 感測器外殼改版 重讀");
        plan.Update(closed, plan.At(Day(plan, -8), close), Manager, "設計案 感測器外殼改版 結案", Status("closed"));

        // 第二筆：第二輪客戶確認中
        var pending = Begin(plan, "控制箱面板改版", "面板改霧面黑，按鍵改成背光", customers[1], -14, out t);
        t = Intake(plan, pending, -13, t);
        t = FirstDrawing(plan, pending, "控制箱面板", -11, t);
        t = CustomerFeedback(plan, pending, customers[1], "控制箱面板", "背光改成白色，按鍵間距加大", -9, t);
        t = SecondDrawing(plan, pending, "控制箱面板", -4, t, setCustomerReview: false);
        var review = plan.At(Day(plan, -3), t);
        plan.Update(pending, review, Designer, "設計案 控制箱面板改版 → 客戶確認中", Status("customer-review"));

        // 第三筆：第一輪設計中
        var designing = Begin(plan, "展示架結構設計", "展示架改成可拆卸，單人十分鐘內組裝完成", customers[2], -2, out t);
        t = Intake(plan, designing, -1, t);
    }

    private static DemoRecord Begin(DemoPlan plan, string name, string requirement, DemoRecord customer, int day, out DateTime at)
    {
        at = plan.At(Day(plan, day));
        return plan.Create(CaseModule, at, Manager, $"設計案 {name} 建立", ctx => new CreateRecordRequest(
            ctx.Crm!.Ids.ModuleKeys["design-case"], ctx.Crm.Values("design-case",
                ("name", name), ("customer", customer.Id), ("owner", Sales), ("designOwner", Designer), ("requirement", requirement))));
    }

    /// <summary>業務完成「確認需求並交設計」，狀態改設計中。</summary>
    private static DateTime Intake(DemoPlan plan, DemoRecord rec, int day, DateTime after)
    {
        var at = plan.At(Day(plan, day), after);
        plan.Run(at, Sales, "設計案 確認需求 完成", (ctx, ct) => CompleteTask(ctx, rec, IntakeTitle, ct));
        var next = plan.At(Day(plan, day), at);
        plan.Update(rec, next, Sales, "設計案 → 設計中", Status("designing"));
        return next;
    }

    /// <summary>設計人員上傳第一版、新增明細、留言並完成繪製，狀態改客戶確認中。</summary>
    private static DateTime FirstDrawing(DemoPlan plan, DemoRecord rec, string prefix, int day, DateTime after)
    {
        var at = plan.At(Day(plan, day), after);
        plan.Run(at, Designer, $"設計案 {prefix} 上傳 v1", (ctx, ct) => Upload(ctx, rec, $"{prefix}-v1.png", DemoDrawings.Png(1), ct));
        var row = plan.At(Day(plan, day), at);
        plan.Update(rec, row, Designer, $"設計案 {prefix} 明細 v1", (ctx, r) => AddDrawingRow(ctx, r, 1, $"{prefix}-v1.png", null));
        var done = plan.At(Day(plan, day), row);
        plan.Run(done, Designer, $"設計案 {prefix} 繪製留言", (ctx, ct) => Comment(ctx, rec, DrawTitle, "第一版完成，請業務確認", [], ct));
        var drawn = plan.At(Day(plan, day), done);
        plan.Run(drawn, Designer, $"設計案 {prefix} 繪製完成", (ctx, ct) => CompleteTask(ctx, rec, DrawTitle, ct));
        var status = plan.At(Day(plan, day), drawn);
        plan.Update(rec, status, Designer, $"設計案 {prefix} → 客戶確認中", Status("customer-review"));
        return status;
    }

    /// <summary>業務記錄客戶回饋（互動紀錄、明細客戶意見、帶兩個附件的留言），完成確認並退回設計。</summary>
    private static DateTime CustomerFeedback(DemoPlan plan, DemoRecord rec, DemoRecord customer, string prefix, string feedback, int day, DateTime after)
    {
        var at = plan.At(Day(plan, day), after);
        var date = Iso(plan.Today.AddDays(day));
        plan.Create("crm-activity", at, Sales, $"設計案 {prefix} 互動紀錄", ctx => new CreateRecordRequest(ctx.Crm!.Ids.ModuleKeys["activity"],
            ctx.Crm.Values("activity", ("subject", "設計稿第一版客戶回饋"), ("kind", ctx.Crm.Opt("activity", "visit")),
                ("date", date), ("customer", customer.Id), ("owner", Sales))));
        var row = plan.At(Day(plan, day), at);
        plan.Refresh(rec, row, Sales, $"設計案 {prefix} 重讀");
        var fb = plan.At(Day(plan, day), row);
        plan.Update(rec, fb, Sales, $"設計案 {prefix} 客戶意見 v1", (ctx, r) => SetFeedback(ctx, r, 0, feedback));
        var comment = plan.At(Day(plan, day), fb);
        plan.Run(comment, Sales, $"設計案 {prefix} 客戶標註留言", (ctx, ct) => Comment(ctx, rec, ConfirmTitle, "@{designer01} 客戶標註如附件",
            [new("客戶標註-v1.png", "image/png", DemoDrawings.Png(1, DemoDrawings.Kind.MarkedUp)),
             new("修改說明.txt", "text/plain", DemoDrawings.Note(feedback))], ct));
        var done = plan.At(Day(plan, day), comment);
        plan.Run(done, Sales, $"設計案 {prefix} 確認完成", (ctx, ct) => CompleteTask(ctx, rec, ConfirmTitle, ct));
        var back = plan.At(Day(plan, day), done);
        plan.Refresh(rec, back, Sales, $"設計案 {prefix} 重讀");
        var status = plan.At(Day(plan, day), back);
        plan.Update(rec, status, Sales, $"設計案 {prefix} → 設計中", Status("designing"));
        return status;
    }

    /// <summary>第二輪：設計人員上傳第二版、新增明細、完成繪製；setCustomerReview 為 true 時一併把狀態改成客戶確認中。</summary>
    private static DateTime SecondDrawing(DemoPlan plan, DemoRecord rec, string prefix, int day, DateTime after, bool setCustomerReview)
    {
        var at = plan.At(Day(plan, day), after);
        plan.Run(at, Designer, $"設計案 {prefix} 上傳 v2", (ctx, ct) => Upload(ctx, rec, $"{prefix}-v2.png", DemoDrawings.Png(2), ct));
        var row = plan.At(Day(plan, day), at);
        plan.Update(rec, row, Designer, $"設計案 {prefix} 明細 v2", (ctx, r) => AddDrawingRow(ctx, r, 2, $"{prefix}-v2.png", null));
        var done = plan.At(Day(plan, day), row);
        plan.Run(done, Designer, $"設計案 {prefix} 繪製完成 v2", (ctx, ct) => CompleteTask(ctx, rec, DrawTitle, ct));
        if (!setCustomerReview) return done;
        var status = plan.At(Day(plan, day), done);
        plan.Update(rec, status, Designer, $"設計案 {prefix} → 客戶確認中", Status("customer-review"));
        return status;
    }

    private static DateTime ConfirmSecondRound(DemoPlan plan, DemoRecord rec, string feedback, int day, DateTime after)
    {
        var at = plan.At(Day(plan, day), after);
        plan.Refresh(rec, at, Sales, "設計案 重讀");
        var row = plan.At(Day(plan, day), at);
        plan.Update(rec, row, Sales, "設計案 客戶意見 v2", (ctx, r) => SetFeedback(ctx, r, 1, feedback));
        var done = plan.At(Day(plan, day), row);
        plan.Run(done, Sales, "設計案 第二輪確認完成", (ctx, ct) => CompleteTask(ctx, rec, ConfirmTitle, ct));
        return done;
    }

    // ---- 單步動作 ----

    private sealed record CommentFile(string Name, string ContentType, byte[] Bytes);

    private static Func<DemoContext, DemoRecord, UpdateRecordRequest> Status(string code) =>
        (ctx, r) => new UpdateRecordRequest(r.Id, ctx.Crm!.Values("design-case", ("status", ctx.Crm.Opt("design-case", code))), r.RowVersion);

    private static UpdateRecordRequest AddDrawingRow(DemoContext ctx, DemoRecord r, int version, string fileName, string? feedback) =>
        new(r.Id, [], r.RowVersion, new()
        {
            [ctx.Crm!.Ids.ModuleKeys["design-drawing"]] =
            [
                new DetailRowInput(null, ctx.Crm.Values("design-drawing",
                    ("version", version), ("fileName", fileName), ("uploader", Designer), ("feedback", feedback)), null)
            ]
        });

    private static UpdateRecordRequest SetFeedback(DemoContext ctx, DemoRecord r, int rowIndex, string feedback)
    {
        var key = ctx.Crm!.Ids.ModuleKeys["design-drawing"];
        var (id, rowVersion) = r.DetailRows[key][rowIndex];
        return new UpdateRecordRequest(r.Id, [], r.RowVersion, new()
        {
            [key] = [new DetailRowInput(id, ctx.Crm.Values("design-drawing", ("feedback", feedback)), rowVersion)]
        });
    }

    private static async Task<ServiceResult> Upload(DemoContext ctx, DemoRecord rec, string name, byte[] bytes, CancellationToken ct)
    {
        var r = await ctx.Sender.Send(new UploadAttachmentRequest(rec.Id, ctx.Crm!.Ids.FieldIds["design-case.drawings"], new MemoryStream(bytes), name), ct);
        if (r.IsSuccess) rec.RowVersion = ((UploadedAttachment)r.Data!).RowVersion;
        return r;
    }

    /// <summary>標題為 title 的任務裡最早一筆還沒完成的。</summary>
    private static async Task<(TaskSummaryResponse? Task, ServiceResult? Failed)> OpenTask(DemoContext ctx, DemoRecord rec, string title, CancellationToken ct)
    {
        var listed = await ctx.Sender.Send(new GetRecordTasksRequest(rec.Id), ct);
        if (!listed.IsSuccess) return (null, listed);
        var task = ((RecordTasksResponse)listed.Data!).Items.Where(t => t.Title == title && t.Status != Domain.Entities.Dynamic.TaskItemStatus.Done).MinBy(t => t.Id);
        return task is null ? (null, ServiceResult.Conflict($"找不到未完成的任務「{title}」")) : (task, null);
    }

    private static async Task<ServiceResult> CompleteTask(DemoContext ctx, DemoRecord rec, string title, CancellationToken ct)
    {
        var (task, failed) = await OpenTask(ctx, rec, title, ct);
        if (failed is not null) return failed;
        return await ctx.Sender.Send(new UpdateTaskRequest(task!.Id, JsonSerializer.SerializeToElement(new { status = "done" })), ct);
    }

    private static async Task<ServiceResult> Comment(DemoContext ctx, DemoRecord rec, string title, string body, CommentFile[] files, CancellationToken ct)
    {
        var (task, failed) = await OpenTask(ctx, rec, title, ct);
        if (failed is not null) return failed;
        return await ctx.Sender.Send(new AddTaskCommentRequest(task!.Id, body,
            [.. files.Select(f => new CommentFileUpload(new MemoryStream(f.Bytes), f.Name, f.ContentType))]), ct);
    }

    private static async Task<ServiceResult> Submit(DemoContext ctx, DemoRecord rec, string comment, CancellationToken ct) =>
        await ctx.Sender.Send(new SubmitApprovalRequest(rec.Id, ctx.Crm!.Ids.OptionIds["design-case.manager-review"], rec.RowVersion, null, comment), ct);

    private static async Task<ServiceResult> Approve(DemoContext ctx, DemoRecord rec, string comment, CancellationToken ct)
    {
        var current = await ctx.Sender.Send(new GetRecordApprovalRequest(rec.Id), ct);
        if (!current.IsSuccess) return current;
        var instance = ((RecordApprovalResponse)current.Data!).Current;
        var task = instance?.Stages.SelectMany(s => s.Tasks).FirstOrDefault(t => t.CanAct);
        if (instance is null || task is null) return ServiceResult.Conflict("沒有可同意的簽核任務");
        return await ctx.Sender.Send(new ApproveTaskRequest(task.Id, instance.RowVersion, comment, null), ct);
    }

    private static DateOnly Day(DemoPlan plan, int offset) => plan.Today.AddDays(offset);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
