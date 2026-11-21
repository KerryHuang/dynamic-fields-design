using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetPagedRecords;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecordHistory;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Application.Dynamic.ReportService.Queries.GetReports;
using DynamicModules.Application.Dynamic.ReportService.Queries.RunReport;
using DynamicModules.Application.Dynamic.SchemaService.Queries.GetMenu;
using DynamicModules.Application.Dynamic.SchemaService.Queries.GetModuleSchema;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Mcp;
using DynamicModules.Domain.Reports;
using MediatR;

namespace DynamicModules.Application.Dynamic.McpService.Tools.Read;

/// <summary>
/// 11 個唯讀工具（mcp1a 計畫判斷 20、21；<c>whoami</c> 在 <see cref="WhoAmITool"/>）。全部只送既有 request、結果以既有 response 原樣
/// 進 <c>data</c>，只做判斷 20 表上的投影；權限與「不提供給 AI」遮罩全在既有 handler 與權限解析，工具不另遮。
/// </summary>
public abstract class ReadTool(ISender sender, string name, string schema, string description) : IMcpTool
{
    protected ISender Sender => sender;
    public McpToolDescriptor Descriptor { get; } = McpToolCatalog.Find(name)!;
    public string InputSchemaJson => schema;
    public string Description => description;
    public abstract Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct);

    /// <summary>
    /// 分頁列表的結果（判斷 14）：<c>meta</c> 有筆數、總數、頁碼、實際頁大小（回應帶回的值）、下一頁；放不下時建議較小的 <c>pageSize</c>，不截斷。
    /// </summary>
    protected static McpToolOutcome Paged<T>(PagedResponse<T> page, Func<T, object> project, Func<T, long>? recordId = null)
    {
        var items = page.Items.Select(project).ToList();
        object Meta(int n) => new
        {
            count = n, total = page.TotalCount, page = page.CurrentPage, pageSize = page.PagesSize, truncated = false,
            next = (long)page.CurrentPage * page.PagesSize < page.TotalCount ? new { page = page.CurrentPage + 1 } : null,
        };
        return McpToolOutcome.Ok(new { items }, Meta(items.Count)) with
        {
            ResultCount = items.Count, RecordIds = recordId is null ? null : page.Items.Select(recordId).ToList(),
            Resize = new McpResize(McpOversize.SuggestPageSize, items.Count, n => (Meta(n), new { items = items.Take(n).ToList() })),
        };
    }

    protected static McpToolOutcome Fail(ServiceResult r) => McpToolOutcome.From(r);
}

public sealed class ListModulesTool(ISender sender)
    : ReadTool(sender, "list_modules", McpTexts.NoArgumentsSchema, McpTexts.ListModulesDescription)
{
    public override async Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct)
    {
        var menu = await Sender.Send(new GetMenuRequest(), ct);
        var items = menu.Select(m => new { key = m.Key, name = m.Name.Resolve(call.Locale), section = m.Section?.Resolve(call.Locale), closed = m.Closed })
            .ToList();
        return McpToolOutcome.Ok(items) with { ResultCount = items.Count };
    }
}

public sealed class DescribeModuleTool(ISender sender)
    : ReadTool(sender, "describe_module", McpTexts.ModuleKeySchema, McpTexts.DescribeModuleDescription)
{
    public override async Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct)
    {
        var a = McpArgs.Of(call.Arguments);
        var key = a.String("moduleKey", required: true, maxLength: 100);
        if (a.HasErrors) return a.Invalid();
        var r = await Sender.Send(new GetModuleSchemaRequest(key!), ct);
        return r.IsSuccess
            ? McpToolOutcome.Ok(r.Data, new { filterSyntax = McpTexts.FilterSyntax }) with { TargetKind = "module", TargetId = key }
            : Fail(r) with { TargetKind = "module", TargetId = key };
    }
}

public sealed class QueryRecordsTool(ISender sender)
    : ReadTool(sender, "query_records", McpTexts.QueryRecordsSchema, McpTexts.QueryRecordsDescription)
{
    public override async Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct)
    {
        var a = McpArgs.Of(call.Arguments);
        var key = a.String("moduleKey", required: true, maxLength: 100);
        var filter = a.Object("filter");
        var keyword = a.String("keyword");
        var page = a.Int("page", 1, int.MaxValue) ?? 1;
        var size = a.PageSize(PagedRequest.MaxPagesSize, call.Limits.MaxRows);
        List<string> sortBy = [], sortOrder = [];
        foreach (var s in a.Array("sort") ?? [])
        {
            if (s.ValueKind != JsonValueKind.Object || !s.TryGetProperty("field", out var f) || f.ValueKind != JsonValueKind.String)
            {
                a.Fail("sort");
                continue;
            }
            sortBy.Add(f.GetString()!);
            sortOrder.Add(s.TryGetProperty("order", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString()! : "asc");
        }
        if (a.HasErrors) return a.Invalid();
        var r = await Sender.Send(new GetPagedRecordsRequest(key!, page, size, filter?.GetRawText(), sortBy, sortOrder, keyword), ct);
        if (!r.IsSuccess) return Fail(r) with { TargetKind = "module", TargetId = key };
        return Paged((PagedResponse<DynamicRecordResponse>)r.Data!,
                x => new { id = x.Id, title = x.Title, values = x.Values, references = x.References, rowVersion = x.RowVersion }, x => x.Id)
            with { TargetKind = "module", TargetId = key };
    }
}

public sealed class GetRecordTool(ISender sender)
    : ReadTool(sender, "get_record", McpTexts.GetRecordSchema, McpTexts.GetRecordDescription)
{
    public override async Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct)
    {
        var a = McpArgs.Of(call.Arguments);
        var id = a.Long("recordId", required: true);
        var details = a.Bool("includeDetails") ?? true;
        if (a.HasErrors) return a.Invalid();
        var r = await Sender.Send(new GetRecordRequest(id!.Value), ct);
        var target = (Kind: "record", Id: id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!r.IsSuccess) return Fail(r) with { TargetKind = target.Kind, TargetId = target.Id };
        var record = (DynamicRecordResponse)r.Data!;
        return McpToolOutcome.Ok(details ? record : record with { Details = null }) with
        {
            TargetKind = target.Kind, TargetId = target.Id, ResultCount = 1, RecordIds = [record.Id],
            OversizeHint = details ? McpTexts.GetRecordOversizeHint : null,
        };
    }
}

public sealed class GetRecordHistoryTool(ISender sender)
    : ReadTool(sender, "get_record_history", McpTexts.RecordHistorySchema, McpTexts.GetRecordHistoryDescription)
{
    public override async Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct)
    {
        var a = McpArgs.Of(call.Arguments);
        var id = a.Long("recordId", required: true);
        var page = a.Int("page", 1, int.MaxValue) ?? 1;
        var size = a.PageSize(PagedRequest.MaxPagesSize, call.Limits.MaxRows);
        if (a.HasErrors) return a.Invalid();
        var r = await Sender.Send(new GetRecordHistoryRequest(id!.Value, page, size, call.Locale), ct);
        var targetId = id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!r.IsSuccess) return Fail(r) with { TargetKind = "record", TargetId = targetId };
        return Paged((PagedResponse<RecordHistoryResponse>)r.Data!, x => x) with { TargetKind = "record", TargetId = targetId };
    }
}

public sealed class ListReportsTool(ISender sender)
    : ReadTool(sender, "list_reports", McpTexts.NoArgumentsSchema, McpTexts.ListReportsDescription)
{
    public override async Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct)
    {
        var r = await Sender.Send(new GetReportsRequest(), ct);
        if (!r.IsSuccess) return Fail(r);
        var items = ((IEnumerable<ReportItem>)r.Data!).Select(x => new { id = x.Id, name = x.Name, moduleKey = x.MainModuleKey }).ToList();
        return McpToolOutcome.Ok(items) with { ResultCount = items.Count };
    }
}

public sealed class RunReportTool(ISender sender)
    : ReadTool(sender, "run_report", McpTexts.RunReportSchema, McpTexts.RunReportDescription)
{
    private static readonly string[] RangeKinds =
        ["today", "thisWeek", "lastWeek", "thisMonth", "lastMonth", "thisYear", "lastYear", "lastDays", "lastWeeks", "lastMonths"];

    public override async Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct)
    {
        var a = McpArgs.Of(call.Arguments);
        var id = a.Long("reportId", required: true);
        var kind = a.String("rangeKind") is { } k ? a.Choice("rangeKind", RangeKinds, k) : null;
        var n = a.Int("rangeN", 1, ReportDateRange.MaxN);
        if (a.HasErrors) return a.Invalid();
        var r = await Sender.Send(new RunReportRequest(id!.Value, kind is null ? null : new ReportDateRange(kind, n), call.Locale), ct);
        var targetId = id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!r.IsSuccess) return Fail(r) with { TargetKind = "report", TargetId = targetId };
        var report = (ReportResult)r.Data!;
        if (report.DetailRows is not { } rows) return McpToolOutcome.Ok(report) with { TargetKind = "report", TargetId = targetId };

        // 明細列（判斷 14）：超過 mcp.maxRows 或放不下時以 ReportRules.TakeDetail 截（TaskIds 同步），標 truncated；沒有續取
        var total = report.DetailTotal ?? rows.Count;
        (object Meta, object Data) Take(int count)
        {
            var cut = count < rows.Count ? ReportRules.TakeDetail(report, count) : report;
            var shown = cut.DetailRows!.Count;
            return (new { truncated = shown < total, rowsReturned = shown, rowsTotal = total }, cut);
        }
        var (meta, data) = Take(Math.Min(rows.Count, call.Limits.MaxRows));
        var shownCount = Math.Min(rows.Count, call.Limits.MaxRows);
        return McpToolOutcome.Ok(data, meta) with
        {
            TargetKind = "report", TargetId = targetId, ResultCount = shownCount,
            Resize = new McpResize(McpOversize.Truncate, shownCount, Take),
        };
    }
}

public sealed class ListMyTasksTool(ISender sender)
    : ReadTool(sender, "list_my_tasks", McpTexts.MyTasksSchema, McpTexts.ListMyTasksDescription)
{
    private static readonly string[] Tabs = ["assigned", "created", "mentioned", "subordinates"];
    private static readonly string[] Filters = ["open", "overdue", "today", "thisWeek", "all"];
    private const int MaxPageSize = 100;

    public override async Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct)
    {
        var a = McpArgs.Of(call.Arguments);
        var tab = a.Choice("tab", Tabs, "assigned");
        var filter = a.Choice("filter", Filters, "open");
        var page = a.Int("page", 1, int.MaxValue) ?? 1;
        // 沒帶 pageSize 時送 maxRows（可能大於 100），交給 handler 夾值，不報錯（判斷 20）
        var size = a.PageSize(MaxPageSize, call.Limits.MaxRows);
        if (a.HasErrors) return a.Invalid();
        var r = await Sender.Send(new GetMyTasksRequest(tab, filter, page, size), ct);
        return r.IsSuccess ? Paged((PagedResponse<TaskSummaryResponse>)r.Data!, x => x) : Fail(r);
    }
}

public sealed class GetTaskTool(ISender sender)
    : ReadTool(sender, "get_task", McpTexts.TaskSchema, McpTexts.GetTaskDescription)
{
    public override async Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct)
    {
        var a = McpArgs.Of(call.Arguments);
        var id = a.Long("taskId", required: true);
        if (a.HasErrors) return a.Invalid();
        var targetId = id!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var task = await Sender.Send(new GetTaskRequest(id.Value), ct);
        if (!task.IsSuccess) return Fail(task) with { TargetKind = "task", TargetId = targetId };
        var comments = await Sender.Send(new GetTaskCommentsRequest(id.Value), ct);
        if (!comments.IsSuccess) return Fail(comments) with { TargetKind = "task", TargetId = targetId };
        // 附件只給中繼資料（設計 §5.5）
        var list = ((IEnumerable<TaskCommentResponse>)comments.Data!).Select(c => new
        {
            c.Id, c.Kind, c.AuthorUserId, c.AuthorName, c.Body, c.Withdrawn, c.SystemEvent, c.SystemData, c.CreatedTime, c.Mentions,
            Attachments = c.Attachments.Select(x => new { id = x.Id, name = x.FileName, size = x.Size }).ToList(),
        }).ToList();
        return McpToolOutcome.Ok(new { task = task.Data, comments = list }) with { TargetKind = "task", TargetId = targetId };
    }
}

public sealed class ListApprovalTasksTool(ISender sender)
    : ReadTool(sender, "list_approval_tasks", McpTexts.ApprovalTasksSchema, McpTexts.ListApprovalTasksDescription)
{
    private const int MaxPageSize = 100;

    public override async Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct)
    {
        var a = McpArgs.Of(call.Arguments);
        var box = a.Choice("box", ApprovalInboxBoxes.Values, ApprovalInboxBoxes.Todo);
        var module = a.String("moduleKey", maxLength: 100);
        var from = a.Date("from");
        var to = a.Date("to");
        var page = a.Int("page", 1, int.MaxValue) ?? 1;
        var size = a.PageSize(MaxPageSize, call.Limits.MaxRows);
        if (a.HasErrors) return a.Invalid();
        var r = await Sender.Send(new GetApprovalTasksRequest(box, module, from, to, page, size), ct);
        return r.IsSuccess ? Paged((PagedResponse<ApprovalInboxItem>)r.Data!, x => x, x => x.RecordId) : Fail(r);
    }
}

public sealed class GetRecordApprovalTool(ISender sender)
    : ReadTool(sender, "get_record_approval", McpTexts.RecordIdSchema, McpTexts.GetRecordApprovalDescription)
{
    public override async Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct)
    {
        var a = McpArgs.Of(call.Arguments);
        var id = a.Long("recordId", required: true);
        if (a.HasErrors) return a.Invalid();
        var targetId = id!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var r = await Sender.Send(new GetRecordApprovalRequest(id.Value), ct);
        return (r.IsSuccess ? McpToolOutcome.Ok(r.Data) with { ResultCount = 1, RecordIds = [id.Value] } : Fail(r))
            with { TargetKind = "record", TargetId = targetId };
    }
}
