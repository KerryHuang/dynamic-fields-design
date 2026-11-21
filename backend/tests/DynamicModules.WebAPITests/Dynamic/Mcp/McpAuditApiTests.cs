using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Dynamic.McpService.Tools;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using static DynamicModules.WebAPITests.Dynamic.Mcp.McpTestKit;

namespace DynamicModules.WebAPITests.Dynamic.Mcp;

/// <summary>稽核表新增丟例外的開關（模擬稽核寫不進去）。</summary>
internal sealed class AuditInsertSwitch : SaveChangesInterceptor
{
    public bool On { get; set; }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (On && eventData.Context?.ChangeTracker.Entries<AuditLogEntry>().Any(e => e.State == EntityState.Added) == true)
            throw new InvalidOperationException("audit table unavailable");
        return ValueTask.FromResult(result);
    }
}

/// <summary>
/// 經正式 <c>/mcp</c> 的稽核（mcp1a 計畫 Task 9、設計 §8.1）：成功、範圍被拒、參數錯誤、速率超限各寫一列；管理員稽核頁、
/// 權杖的呼叫紀錄與今日呼叫數查得到；稽核寫不進去時不回任何記錄值。
/// </summary>
[Trait("Category", "Postgres")]
public class McpAuditApiTests : IAsyncLifetime
{
    private TestApp _app = null!;
    private HttpClient _admin = null!;
    private readonly AuditInsertSwitch _audit = new();
    private string _module = "";

    public async Task InitializeAsync()
    {
        _app = NewApp();
        _app.Interceptors.Add(_audit);
        EnableMcp(_app);
        _admin = _app.AsUser();
        _module = await CreateModule(_admin);
        var title = await TitleKey(_admin, _module);
        await Activate(_admin, _module);
        foreach (var t in new[] { "甲單", "乙單", "丙單" })
            (await _admin.PostAsJsonAsync($"/api/v3/dynamic/modules/{_module}/records",
                new { values = new Dictionary<string, object> { [title] = t } })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _app.Dispose();
        return Task.CompletedTask;
    }

    private static Dictionary<string, object?> Args(object args) =>
        JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!;

    private static string? Code(CallToolResult r) =>
        r.StructuredContent!.Value.TryGetProperty("error", out var e) ? e.GetProperty("code").GetString() : null;

    [Fact]
    public async Task FourOutcomes_FourRows_VisibleToAdminAndOwner()
    {
        var (raw, tokenId) = IssueToken(_app, "admin");
        var (designRaw, designId) = IssueToken(_app, "admin", scopes: Domain.Mcp.McpScopes.DesignRead);
        await using var client = await ConnectAsync(_app, raw);
        await using var design = await ConnectAsync(_app, designRaw);

        Code(await client.CallToolAsync("query_records", Args(new { moduleKey = _module }))).Should().BeNull();
        Code(await design.CallToolAsync("query_records", Args(new { moduleKey = _module }))).Should().Be(McpErrorCodes.ScopeDenied);
        Code(await client.CallToolAsync("query_records", Args(new { moduleKey = _module, pageSize = 0 }))).Should().Be(McpErrorCodes.InvalidArguments);
        SetPolicy(_app, PolicyCatalog.McpCallsPerMinute, "2");   // 每把權杖：這把已用 2 次
        Code(await client.CallToolAsync("query_records", Args(new { moduleKey = _module }))).Should().Be(McpErrorCodes.RateLimited);

        var rows = Query(_app, db => db.Set<AuditLogEntry>().AsNoTracking()
            .Where(a => (a.TokenId == tokenId || a.TokenId == designId) && a.Action == "mcp.queryRecords").OrderBy(a => a.Id).ToList());
        rows.Select(a => (a.TokenId, a.Result, a.RiskLevel, a.Channel, a.ReasonCode)).Should().Equal(
            (tokenId, AuditResult.Success, AuditRiskLevel.Read, AuditChannel.Mcp, (string?)null),
            (designId, AuditResult.Denied, AuditRiskLevel.Read, AuditChannel.Mcp, McpErrorCodes.ScopeDenied),
            (tokenId, AuditResult.Invalid, AuditRiskLevel.Read, AuditChannel.Mcp, McpErrorCodes.InvalidArguments),
            (tokenId, AuditResult.Denied, AuditRiskLevel.Read, AuditChannel.Mcp, McpErrorCodes.RateLimited));
        rows[0].ResultCount.Should().Be(3);
        rows.Should().OnlyContain(a => a.ArgumentsHash != null && a.TargetLabel == "query_records" && a.ActorUserId == "admin");

        var listed = await JsonOf(await _admin.GetAsync("/api/v3/dynamic/system/ops/audit?channel=mcp&limit=50"));
        foreach (var id in rows.Select(r => r.Id)) listed.GetRawText().Should().Contain($"\"id\":{id},");

        var calls = await JsonOf(await _admin.GetAsync($"/api/v3/dynamic/me/mcp/tokens/{tokenId}/calls"));
        calls.EnumerateArray().Where(c => c.GetProperty("action").GetString() == "mcp.queryRecords").Should().HaveCount(3);

        var tokens = await JsonOf(await _admin.GetAsync("/api/v3/dynamic/me/mcp/tokens"));
        var today = Query(_app, db => db.Set<AuditLogEntry>().Count(a => a.TokenId == tokenId && a.Action.StartsWith("mcp.") && a.Action != "mcp.access"));
        tokens.EnumerateArray().Single(t => t.GetProperty("id").GetInt64() == tokenId).GetProperty("todayCalls").GetInt32().Should().Be(today);
    }

    [Fact]
    public async Task AuditUnavailable_NoRecordValues()
    {
        await using var client = await ConnectAsync(_app, IssueToken(_app, "admin").Raw);
        _audit.On = true;
        var r = await client.CallToolAsync("query_records", Args(new { moduleKey = _module }));
        _audit.On = false;

        r.IsError.Should().BeTrue();
        Code(r).Should().Be(McpErrorCodes.AuditUnavailable);
        var text = ((TextContentBlock)r.Content.Single()).Text + r.StructuredContent!.Value.GetRawText();
        foreach (var v in new[] { "甲單", "乙單", "丙單", "\\u7532" }) text.Should().NotContain(v);
    }
}
