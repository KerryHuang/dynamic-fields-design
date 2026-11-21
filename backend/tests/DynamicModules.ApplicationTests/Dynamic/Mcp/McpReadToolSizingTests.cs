using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.McpService.Tools;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecordHistory;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Application.Dynamic.ReportService.Queries.RunReport;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Mcp;

/// <summary>替身資料來源：記下每次收到的頁大小，照頁碼與頁大小切 120 筆、每筆約 1,000 字的假資料。</summary>
public sealed class SizingSource
{
    public const int Total = 120;
    public static readonly string Filler = new('字', 1000);
    public List<int> PageSizes { get; } = [];
    public ReportResult? Report { get; set; }

    public PagedResponse<T> Page<T>(int page, int size, Func<long, T> make)
    {
        PageSizes.Add(size);
        var items = Enumerable.Range(1, Total).Skip((page - 1) * size).Take(size).Select(i => make(i)).ToList();
        return new PagedResponse<T>(items, Total, page, size);
    }
}

public sealed class StubHistory(SizingSource s) : IRequestHandler<GetRecordHistoryRequest, ServiceResult>
{
    public Task<ServiceResult> Handle(GetRecordHistoryRequest r, CancellationToken ct) => Task.FromResult(ServiceResult.Success(s.Page(r.CurrentPage, r.PagesSize,
        id => new RecordHistoryResponse(id, "update", "f_1", null, "", SizingSource.Filler, "admin", "管理員", DateTime.UnixEpoch, null))));
}

public sealed class StubMyTasks(SizingSource s) : IRequestHandler<GetMyTasksRequest, ServiceResult>
{
    public Task<ServiceResult> Handle(GetMyTasksRequest r, CancellationToken ct) => Task.FromResult(ServiceResult.Success(s.Page(r.CurrentPage, r.PagesSize,
        id => new TaskSummaryResponse(id, SizingSource.Filler, TaskItemStatus.Todo, "admin", "管理員", true, null, false, null, false, false, false, false,
            "v", null, null, "task", null, null))));
}

public sealed class StubApprovals(SizingSource s) : IRequestHandler<GetApprovalTasksRequest, ServiceResult>
{
    public Task<ServiceResult> Handle(GetApprovalTasksRequest r, CancellationToken ct) => Task.FromResult(ServiceResult.Success(s.Page(r.CurrentPage, r.PagesSize,
        id => new ApprovalInboxItem(id, id, id, "m", new I18nText(), SizingSource.Filler, null, "admin", "管理員", DateTime.UnixEpoch, null, false, false, "pending"))));
}

public sealed class StubReport(SizingSource s) : IRequestHandler<RunReportRequest, ServiceResult>
{
    public Task<ServiceResult> Handle(RunReportRequest r, CancellationToken ct) => Task.FromResult(ServiceResult.Success(s.Report!));
}

/// <summary>
/// 分頁工具的 <c>retry.pageSize</c> 與報表明細截斷（mcp1a 計畫判斷 14、20、Task 9）：走正式的工具、參數解析、執行器與格式化，只換掉 request handler。
/// （計畫寫在 WebAPI 層用 <c>ExtraServices</c> 換 handler；放在這層同樣走正式路徑、少起一個 host。）
/// </summary>
[Trait("Category", "Postgres")]
public class McpReadToolSizingTests : McpInvokerTestBase
{
    private readonly SizingSource _source = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton(_source);
        services.AddTransient<IRequestHandler<GetRecordHistoryRequest, ServiceResult>, StubHistory>();
        services.AddTransient<IRequestHandler<GetMyTasksRequest, ServiceResult>, StubMyTasks>();
        services.AddTransient<IRequestHandler<GetApprovalTasksRequest, ServiceResult>, StubApprovals>();
        services.AddTransient<IRequestHandler<RunReportRequest, ServiceResult>, StubReport>();
    }

    private static JsonElement Error(McpFormattedResult r) => r.Structured.GetProperty("error");

    [Theory]
    [InlineData("get_record_history", """{"recordId":1""")]
    [InlineData("list_my_tasks", "{")]
    [InlineData("list_approval_tasks", "{")]
    public async Task RetryPageSize_Completes(string tool, string argsPrefix)
    {
        string Args(string extra) => argsPrefix + (argsPrefix.Length > 1 && extra.Length > 0 ? "," : "") + extra + "}";

        var first = await Invoke(tool, Args(""));
        CodeOf(first).Should().Be(McpErrorCodes.ResultTooLarge);
        var size = Error(first).GetProperty("retry").GetProperty("pageSize").GetInt32();
        size.Should().BeInRange(1, 39);

        var ids = new List<long>();
        _source.PageSizes.Clear();
        for (var page = 1; ; page++)
        {
            var r = await Invoke(tool, Args($"\"page\":{page},\"pageSize\":{size}"));
            CodeOf(r).Should().BeNull($"第 {page} 頁照建議頁大小應放得下");
            var data = r.Structured.GetProperty("data").GetProperty("items");
            ids.AddRange(data.EnumerateArray().Select(i => i.GetProperty(i.TryGetProperty("taskId", out _) ? "taskId" : "id").GetInt64()));
            if (!r.Structured.GetProperty("meta").TryGetProperty("next", out var next) || next.ValueKind == JsonValueKind.Null) break;
        }

        ids.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(Enumerable.Range(1, SizingSource.Total).Select(i => (long)i));
        _source.PageSizes.Should().OnlyContain(s => s == size);
    }

    private static ReportResult Report(int rows, int cellChars, IReadOnlyList<ReportGroupResult>? groups = null) => new(
        "ok", null, "main", [], groups, null,
        groups is null ? Enumerable.Range(1, rows).Select(i => (IReadOnlyList<object?>)[i, new string('x', cellChars)]).ToList() : null,
        false, new DateOnly(2026, 11, 3), null, DetailTotal: groups is null ? rows : null,
        TaskIds: groups is null ? Enumerable.Range(1, rows).Select(i => (long)i).ToList() : null);

    [Fact]
    public async Task RunReport_DetailOverMaxRows_TruncatedWithTaskIds()
    {
        await SetSystem(PolicyCatalog.McpMaxRows, "3");
        _source.Report = Report(10, 10);

        var r = await Invoke("run_report", """{"reportId":1}""");

        CodeOf(r).Should().BeNull();
        var (meta, data) = (r.Structured.GetProperty("meta"), r.Structured.GetProperty("data"));
        (meta.GetProperty("truncated").GetBoolean(), meta.GetProperty("rowsReturned").GetInt32(), meta.GetProperty("rowsTotal").GetInt32())
            .Should().Be((true, 3, 10));
        data.GetProperty("detailRows").GetArrayLength().Should().Be(3);
        data.GetProperty("taskIds").EnumerateArray().Select(e => e.GetInt64()).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task RunReport_DetailTooLarge_CutToFit()
    {
        _source.Report = Report(100, 1000);

        var r = await Invoke("run_report", """{"reportId":1}""");

        CodeOf(r).Should().BeNull();
        var meta = r.Structured.GetProperty("meta");
        meta.GetProperty("truncated").GetBoolean().Should().BeTrue();
        var shown = meta.GetProperty("rowsReturned").GetInt32();
        shown.Should().BeInRange(1, 39);
        r.Structured.GetProperty("data").GetProperty("taskIds").GetArrayLength().Should().Be(shown);
        r.Size.Should().BeLessThanOrEqualTo(40_000);
    }

    [Fact]
    public async Task RunReport_GroupedTooLarge_ResultTooLarge()
    {
        var groups = Enumerable.Range(1, 100)
            .Select(i => new ReportGroupResult([i.ToString()], [new string('g', 1000) + i], new Dictionary<string, decimal?> { ["n"] = i })).ToList();
        _source.Report = Report(0, 0, groups);

        var r = await Invoke("run_report", """{"reportId":1}""");

        CodeOf(r).Should().Be(McpErrorCodes.ResultTooLarge);
        r.Structured.GetRawText().Should().NotContain("ggggg");
    }

    [Fact]
    public async Task RunReport_RangeKind_Invalid()
    {
        _source.Report = Report(1, 1);
        CodeOf(await Invoke("run_report", """{"reportId":1,"rangeKind":"forever"}""")).Should().Be(McpErrorCodes.InvalidArguments);
        CodeOf(await Invoke("run_report", """{"reportId":1,"rangeKind":"lastDays","rangeN":0}""")).Should().Be(McpErrorCodes.InvalidArguments);
        CodeOf(await Invoke("run_report", """{"reportId":1,"rangeKind":"lastDays","rangeN":7}""")).Should().BeNull();
    }
}
