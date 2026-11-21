using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Dynamic.McpService;
using DynamicModules.Application.Dynamic.McpService.Tools;
using DynamicModules.Application.Dynamic.McpService.WriteLoop;
using DynamicModules.Application.Dynamic.McpService.Writes;
using DynamicModules.Application.Dynamic.OpsService.Alerts;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic.Mcp;

/// <summary>讓迴圈事件請求丟例外（<c>IncidentWriteFails_StillWriteLoop</c>）。</summary>
public sealed class LoopIncidentFault { public bool On { get; set; } }

/// <summary>告警信由 WebAPI 實作；這裡的事件不寄信（出廠不寄客戶設定類），只要容器建得起來。</summary>
public sealed class NoAlertMail : IAlertMailSender, IAlertSecretProtector
{
    public Task<MailSendResult> SendAsync(SmtpSettings settings, string? password, AlertMail mail, CancellationToken ct) =>
        Task.FromResult(MailSendResult.Success);
    public string Protect(string plain) => plain;
    public string? TryUnprotect(string protectedText) => protectedText;
}

public sealed class TestOpsEnvironment : DynamicModules.Application.Dynamic.OpsService.IOpsEnvironment
{
    public string Version => "test";
    public string Commit => "test";
    public string BuiltAt => "test";
    public string Edition => "OnPrem";
}

public sealed class ThrowingLoopIncidentBehavior(LoopIncidentFault fault) : IPipelineBehavior<RecordMcpWriteLoopIncidentRequest, ServiceResult>
{
    public Task<ServiceResult> Handle(RecordMcpWriteLoopIncidentRequest request, RequestHandlerDelegate<ServiceResult> next, CancellationToken ct) =>
        fault.On ? throw new InvalidOperationException("incident boom") : next();
}

/// <summary>
/// 寫入迴圈偵測（mcp2a 計畫判斷 8、D4、D5；設計 §20 C3）：同一權杖在時間窗內成功寫同一筆記錄達門檻就擋下、開維運事件，
/// 時間窗滑過自動恢復。門檻 5、時間窗 10 分鐘，用手動時鐘控時間。用正式的 <c>update_record</c> 工具寫真的記錄。
/// </summary>
[Trait("Category", "Postgres")]
public class McpWriteLoopTests : McpWriteTestBase
{
    private const string UpdateRecord = "update_record";
    private readonly LoopIncidentFault _incidentFault = new();
    private readonly WriteScopeSpy _writes = new();
    private string _titleKey = "";
    private McpToken _token = null!;

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton(_incidentFault);
        services.AddScoped<IMcpWriteScope>(sp => new SpyingWriteScope(sp.GetRequiredService<McpWriteScope>(), _writes));
        services.AddSingleton<IAlertMailSender, NoAlertMail>();
        services.AddSingleton<IAlertSecretProtector, NoAlertMail>();
        services.AddSingleton(AlertMailOptions.None);
        services.AddSingleton<DynamicModules.Application.Dynamic.OpsService.IOpsEnvironment, TestOpsEnvironment>();
        services.AddTransient<IPipelineBehavior<RecordMcpWriteLoopIncidentRequest, ServiceResult>, ThrowingLoopIncidentBehavior>();
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _token = AddToken("admin", "筆電上的 Claude", NowUtc.AddDays(10));
        Channel.TokenId = _token.Id;
        _titleKey = $"f_{(await Detail("mcpconf")).TitleFieldId}";
        await SetSystem(PolicyCatalog.OpsRuleLoopTriggersPerRecord, "5");
        await SetSystem(PolicyCatalog.OpsRuleLoopWindowMinutes, "10");
    }

    private async Task<McpFormattedResult> Run(string tool, string args)
    {
        using var origin = AuditOrigin.Begin(AuditChannel.Mcp, Channel.TokenId);
        using var scope = NewScope();
        Db.Context.ChangeTracker.Clear();
        try
        {
            return await scope.ServiceProvider.GetRequiredService<McpToolInvoker>()
                .InvokeAsync(tool, JsonDocument.Parse(args).RootElement.Clone(), new McpCallContext("test-client", "1.0"), default);
        }
        finally { Db.Context.ChangeTracker.Clear(); }
    }

    private string RowVersionOf(long id) => Db.Context.Set<Record>().AsNoTracking().Single(r => r.Id == id).RowVersion;

    private string UpdateArgs(long record, string value, string? key = null, string? rowVersion = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["recordId"] = record, ["rowVersion"] = rowVersion ?? RowVersionOf(record),
            ["values"] = new Dictionary<string, object> { [_titleKey] = value }, ["idempotencyKey"] = key ?? NewKey(),
        });

    private Task<McpFormattedResult> Update(long record, string value, string? key = null, string? rowVersion = null) =>
        Run(UpdateRecord, UpdateArgs(record, value, key, rowVersion));

    /// <summary>連續成功改 n 次；回傳每次的 key。</summary>
    private async Task<List<string>> Succeed(int n, long? record = null)
    {
        var keys = new List<string>();
        for (var i = 0; i < n; i++)
        {
            var key = NewKey();
            var r = await Update(record ?? Target, $"v{i}-{key[..6]}", key);
            r.IsError.Should().BeFalse(r.Text);
            keys.Add(key);
        }
        return keys;
    }

    private Task<List<AuditLogEntry>> Rows() =>
        Db.Context.Set<AuditLogEntry>().AsNoTracking().Where(a => a.TokenId == Channel.TokenId && a.Channel == AuditChannel.Mcp)
            .OrderBy(a => a.Id).ToListAsync();

    private List<OpsIncident> Incidents() =>
        Db.Context.Set<OpsIncident>().AsNoTracking().Where(i => i.Code == McpWriteLoopIncident.Code).ToList();

    private int IncidentNotifications() =>
        Db.Context.Set<Notification>().AsNoTracking().Count(n => n.Kind == NotificationKinds.OpsIncident);

    [Fact]
    public async Task FifthWrite_Allowed_SixthBlocked()
    {
        await Succeed(5);
        var before = ValuesOf(Target);

        var r = await Update(Target, "sixth");

        CodeOf(r).Should().Be(McpErrorCodes.WriteLoop);
        r.Structured.GetProperty("error").GetProperty("message").GetString().Should().Contain("10").And.Contain("5");
        ValuesOf(Target).Should().Be(before);
    }

    [Fact]
    public async Task Blocked_ExactlyOneDeniedRow_NoSuccessRow()
    {
        await Succeed(5);
        var before = (await Rows()).Count;
        _writes.Reset();

        await Update(Target, "sixth");

        _writes.CommittedAnswers.Should().NotBeEmpty().And.OnlyContain(c => !c, "被擋的呼叫沒有提交（IsCommitted 為 false）");

        var added = (await Rows()).Skip(before).ToList();
        var row = added.Should().ContainSingle().Subject;
        (row.Action, row.Result, row.ReasonCode, row.TargetKind, row.TargetId, row.TargetLabel)
            .Should().Be(("mcp.updateRecord", AuditResult.Denied, McpErrorCodes.WriteLoop, McpWriteTargets.Record, Target.ToString(), UpdateRecord));
    }

    [Fact]
    public async Task Blocked_ToolNotCalled_NotArmed()
    {
        await Succeed(5);
        Write.ToolCalled = false;
        var args = WriteArgs("x");
        _writes.Reset();

        var r = await CallWrite(args);

        CodeOf(r).Should().Be(McpErrorCodes.WriteLoop, "測試寫入工具也以 recordId 為迴圈目標，與 update_record 合併計數");
        Write.ToolCalled.Should().BeFalse();
        _writes.Arms.Should().Be(0, "迴圈檢查在上膛之前");
    }

    [Fact]
    public async Task OtherRecord_NotBlocked()
    {
        await Succeed(5);
        (await Update(RecordA, "other")).IsError.Should().BeFalse();
    }

    [Fact]
    public async Task OtherToken_NotBlocked()
    {
        await Succeed(5);
        Channel.TokenId = AddToken("admin", "另一把", NowUtc.AddDays(10)).Id;

        (await Update(Target, "other token")).IsError.Should().BeFalse();
    }

    [Fact]
    public async Task Replays_NotCounted()
    {
        var key = NewKey();
        var args = UpdateArgs(Target, "same", key);
        for (var i = 0; i < 10; i++)
        {
            var r = await Run(UpdateRecord, args);
            r.IsError.Should().BeFalse(r.Text);
        }

        (await Update(Target, "after replays")).IsError.Should().BeFalse("回放不計入迴圈次數");
    }

    [Fact]
    public async Task ResendAfterBlock_Replays()
    {
        var first4 = await Succeed(4);
        var k5 = NewKey();
        var args5 = UpdateArgs(Target, "fifth", k5);
        (await Run(UpdateRecord, args5)).IsError.Should().BeFalse();
        CodeOf(await Update(Target, "sixth")).Should().Be(McpErrorCodes.WriteLoop);

        var resend = await Run(UpdateRecord, args5);

        resend.IsError.Should().BeFalse(resend.Text);
        resend.Structured.GetProperty("meta").GetProperty("replayed").GetBoolean().Should().BeTrue();
        first4.Should().HaveCount(4);
    }

    [Fact]
    public async Task Failures_NotCounted()
    {
        for (var i = 0; i < 10; i++)
            CodeOf(await Update(Target, $"stale{i}", rowVersion: "not-the-version")).Should().Be(McpErrorCodes.Stale);

        (await Update(Target, "after failures")).IsError.Should().BeFalse();
    }

    /// <summary>
    /// 預覽與送簽合併計數：記錄沒有簽核流程，這裡直接放 <c>submit_approval</c> 的稽核列（執行器與 behavior 寫的形狀，見 mcp2a-3 交棒）；
    /// 端到端的送簽在 WebAPI 的寫入工具測試。
    /// </summary>
    private async Task AddSubmitRows(int n, string? reason)
    {
        for (var i = 0; i < n; i++)
            Db.Context.Add(new AuditLogEntry
            {
                OccurredUtc = NowUtc, Action = "mcp.submitApproval", RiskLevel = AuditRiskLevel.Write, Result = AuditResult.Success,
                Channel = AuditChannel.Mcp, ActorKind = AuditActorKind.User, ActorUserId = "admin", TokenId = Channel.TokenId,
                TargetKind = McpWriteTargets.Record, TargetId = Target.ToString(), TargetLabel = "submit_approval", ReasonCode = reason,
            });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
    }

    [Fact]
    public async Task PreviewOnly_NotCounted()
    {
        await AddSubmitRows(10, McpAuditReasons.Preview);

        (await Update(Target, "after previews")).IsError.Should().BeFalse();
    }

    [Fact]
    public async Task SubmitApproval_Counts()
    {
        await AddSubmitRows(2, null);
        await Succeed(3);

        CodeOf(await Update(Target, "sixth")).Should().Be(McpErrorCodes.WriteLoop);
        // submit_approval 也以 recordId 為目標：被擋在工具之前（不需要簽核流程）
        var submit = await Run("submit_approval", JsonSerializer.Serialize(new { recordId = Target, to = 1, rowVersion = RowVersionOf(Target),
            idempotencyKey = NewKey() }));
        CodeOf(submit).Should().Be(McpErrorCodes.WriteLoop);
    }

    [Fact]
    public async Task CreateRecord_NotChecked()
    {
        await Succeed(5);
        var r = await Run("create_record", JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["moduleKey"] = "mcpconf", ["values"] = new Dictionary<string, object> { [_titleKey] = "new" }, ["idempotencyKey"] = NewKey(),
        }));

        r.IsError.Should().BeFalse(r.Text);
    }

    [Fact]
    public async Task Blocked_NotCounted_RecoversAfterWindow()
    {
        await Succeed(1);
        Clock.Advance(TimeSpan.FromMinutes(5));
        await Succeed(4);
        CodeOf(await Update(Target, "blocked1")).Should().Be(McpErrorCodes.WriteLoop);
        CodeOf(await Update(Target, "blocked2")).Should().Be(McpErrorCodes.WriteLoop);

        // 最早那筆在 5 分鐘前＋10 分鐘的窗：再過 5 分鐘 1 秒就滑出窗外，成功次數降到 4
        Clock.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)));

        (await Update(Target, "recovered")).IsError.Should().BeFalse("被擋的兩次不計入，最早那筆滑出就恢復");
        CodeOf(await Update(Target, "again")).Should().Be(McpErrorCodes.WriteLoop, "恢復一次後又到門檻（鋸齒效應）");
    }

    [Fact]
    public async Task Incident_Opened_Deduped()
    {
        await Succeed(5);
        var notificationsBefore = IncidentNotifications();

        await Update(Target, "blocked1");

        Logs.Where(l => l.Level >= LogLevel.Warning).Should().BeEmpty();
        var incident = Incidents().Should().ContainSingle().Subject;
        (incident.Source, incident.Category, incident.Severity, incident.TitleKey, incident.OccurrenceCount)
            .Should().Be((IncidentSource.Config, IncidentCategory.Security, IncidentSeverity.Warning, IncidentTitles.McpWriteLoop, 1L),
                "安全類（處理者是管理員），來源維持客戶設定：只在新開與回歸時通知（第 4 批審查 S1）");
        var p = IncidentTitles.FromJson(incident.TitleParamsJson)!;
        p.Keys.Should().BeEquivalentTo("tokenId", "recordId", "count", "minutes");
        (p["tokenId"], p["recordId"], p["count"], p["minutes"]).Should().Be(((object)_token.Id, (object)Target, (object)5L, (object)10L));
        var notified = IncidentNotifications() - notificationsBefore;
        notified.Should().BeGreaterThan(0, "出廠的 ops.alertRecipients 是系統管理員");
        Db.Context.Set<Notification>().AsNoTracking().Where(n => n.Kind == NotificationKinds.OpsIncident).Select(n => n.UserId).ToList()
            .Should().Contain("admin");

        await Update(Target, "blocked2");

        Incidents().Should().ContainSingle().Which.OccurrenceCount.Should().Be(2);
        (IncidentNotifications() - notificationsBefore).Should().Be(notified, "同指紋再發生只累加次數、不再通知");
    }

    /// <summary>安全類：收件人走 ops.alertRecipients（不是 ops.configIssueRecipients）；出廠的告警信類別含 security，所以新開時排一封告警信，再發生不再排。</summary>
    [Fact]
    public async Task Incident_Security_AlertRecipients_MailOnOpenOnly()
    {
        await SetSystem(PolicyCatalog.OpsAlertRecipients, "user:wenhui");
        await Succeed(5);

        await Update(Target, "blocked1");

        var incident = Incidents().Should().ContainSingle().Subject;
        Db.Context.Set<Notification>().AsNoTracking().Where(n => n.Kind == NotificationKinds.OpsIncident).Select(n => n.UserId).Distinct().ToList()
            .Should().Equal("wenhui");
        int Mails() => Db.Context.Set<AlertOutbox>().AsNoTracking().Count(m => m.IncidentId == incident.Id);
        Mails().Should().Be(1);

        await Update(Target, "blocked2");

        Mails().Should().Be(1, "同指紋再發生（Updated）不通知、不寄信");
    }

    [Fact]
    public async Task Incident_TitleParams_NoUserText()
    {
        const string marker = "ZZ-秘密權杖名稱-ZZ";
        Db.Context.Set<McpToken>().Single(t => t.Id == _token.Id).Name = marker;
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        await Succeed(5);

        await Update(Target, "blocked");

        var incident = Incidents().Should().ContainSingle().Subject;
        (incident.TitleParamsJson ?? "").Should().NotContain(marker);
        (incident.NormalizedMessage ?? "").Should().NotContain(marker);
        Db.Context.Set<Notification>().AsNoTracking().Where(n => n.Kind == NotificationKinds.OpsIncident).Select(n => n.TitleSnapshot).ToList()
            .Should().NotBeEmpty().And.OnlyContain(t => !t.Contains(marker));
    }

    [Fact]
    public async Task IncidentWriteFails_StillWriteLoop()
    {
        await Succeed(5);
        _incidentFault.On = true;
        var before = (await Rows()).Count;

        var r = await Update(Target, "blocked");

        CodeOf(r).Should().Be(McpErrorCodes.WriteLoop);
        var row = (await Rows()).Skip(before).Should().ContainSingle().Subject;
        (row.Result, row.ReasonCode).Should().Be((AuditResult.Denied, McpErrorCodes.WriteLoop));
        Incidents().Should().BeEmpty();
        Logs.Count(l => l.Level == LogLevel.Error).Should().Be(1);
    }

    [Fact]
    public async Task PolicyChange_TakesEffectNextCall()
    {
        await SetSystem(PolicyCatalog.OpsRuleLoopTriggersPerRecord, "6");
        await Succeed(5);

        (await Update(Target, "sixth allowed")).IsError.Should().BeFalse();
        await SetSystem(PolicyCatalog.OpsRuleLoopTriggersPerRecord, "5");

        CodeOf(await Update(Target, "now blocked")).Should().Be(McpErrorCodes.WriteLoop);
    }

    [Fact]
    public async Task BlockedCall_CountsTowardDailyQuota()
    {
        await Succeed(5);
        await Update(Target, "blocked");

        using var scope = NewScope();
        (await scope.ServiceProvider.GetRequiredService<McpUsage>().CountTodayAsync(_token.Id, default)).Should().Be(6);
    }
}

/// <summary>記錄執行器對寫入範圍的上膛次數與「已提交嗎」的回答（第 4 批審查 S8）。</summary>
public sealed class WriteScopeSpy
{
    public int Arms { get; set; }
    public List<bool> CommittedAnswers { get; } = [];
    public void Reset() { Arms = 0; CommittedAnswers.Clear(); }
}

internal sealed class SpyingWriteScope(McpWriteScope inner, WriteScopeSpy spy) : IMcpWriteScope
{
    public void Arm(long call, McpWriteAudit audit) { spy.Arms++; inner.Arm(call, audit); }
    public void Disarm() => inner.Disarm();
    public bool IsArmed => inner.IsArmed;
    public bool IsCommitted(long call)
    {
        var committed = inner.IsCommitted(call);
        spy.CommittedAnswers.Add(committed);
        return committed;
    }
}
