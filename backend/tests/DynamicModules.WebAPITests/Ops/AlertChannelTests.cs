using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Dynamic.OpsService.Alerts;
using DynamicModules.Application.Dynamic.OpsService.Health;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Application.Dynamic.OpsService.Scheduling;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using static DynamicModules.WebAPITests.Ops.AlertKit;
using static DynamicModules.WebAPITests.Ops.OpsIncidentKit;
using static DynamicModules.WebAPITests.Ops.SchedulerTestKit;

namespace DynamicModules.WebAPITests.Ops;

/// <summary>告警通道：寄送佇列、重試、節流與摘要、通道設定與測試信、直送（維運設計 §17.2.6、§17.8 C13、D01–D13）。</summary>
[Trait("Category", "Postgres")]
public class AlertChannelTests
{
    /// <summary>從整點後 10 分開始（摘要信在下一個整點後才寄）。</summary>
    private static readonly DateTimeOffset T0 = Start.AddMinutes(10);

    private static DateTime Now(TestApp app) => app.Time.GetUtcNow().UtcDateTime;

    private static async Task<long> Open(TestApp app, string fingerprint, IncidentSeverity severity = IncidentSeverity.Warning) =>
        (await Record(app, Infra(fingerprint, Now(app), severity))).IncidentId!.Value;

    private static async Task<JsonElement> Json(HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task D01_InfraWarning_OneOutboxRow_SentByOpsMonitor_TextFromTitleKeyAndWhitelistOnly()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);
        SetTime(app, T0);
        await Configure(app);
        var id = await Open(app, "fp-d01");

        var row = (await Outbox(app)).Should().ContainSingle().Subject;
        row.IncidentId.Should().Be(id);
        row.Transition.Should().Be(AlertTransition.Opened);
        row.Status.Should().Be(AlertOutboxStatus.Pending);

        // 經 ops.monitor 排程工作送出（寄送者在健康檢查之前跑）
        var clock = new ManualClock(T0);
        var runner = Runner(app, clock, app.Services.GetServices<IScheduledJob>().Where(j => j.Key == "ops.monitor"));
        await runner.EnsureJobsAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(61));
        await runner.TickAsync(CancellationToken.None);

        (await Row(app, "ops.monitor")).LastStatus.Should().Be(ScheduledJobStatus.Succeeded);
        var sent = mail.Sent.Should().ContainSingle().Subject;
        sent.Subject.Should().StartWith("[Formwright][警告] ").And.EndWith($"（#{id}）");
        sent.Subject.Should().Be(row.Subject, "寄的是寫入時的快照");
        sent.Body.Should().Contain("類別：基礎設施").And.Contain("狀況：新發生").And.Contain("版本：test").And.NotContain("secret-normalized-message");
        mail.Calls.Single().Password.Should().Be("s3cret-pw", "寄送時才解密");
        mail.Calls.Single().Settings.ToAddresses.Should().Equal("a@test.invalid", "b@test.invalid");
        row = (await Outbox(app)).Single();
        row.Status.Should().Be(AlertOutboxStatus.Sent);
        row.Attempts.Should().Be(1);
        row.SentUtc.Should().NotBeNull();
        (await ChannelRow(app))!.LastSuccessUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task D02_AlwaysFails_Retries1_5_15_60_ThenFailed_ChannelIncidentNotQueued_LaterSuccessResolves()
    {
        var mail = new FakeMailSender { Result = _ => new MailSendResult(false, MailSendResult.Auth, 535) };
        using var app = App(mail);
        SetTime(app, T0);
        await Configure(app);
        await Open(app, "fp-d02");

        foreach (var wait in new[] { 0, 1, 5, 15, 60 })
        {
            if (wait > 0)
            {
                // 還沒到時間不重試
                app.Time.Advance(TimeSpan.FromMinutes(wait) - TimeSpan.FromSeconds(1));
                await Dispatch(app);
                app.Time.Advance(TimeSpan.FromSeconds(1));
            }
            var before = mail.Calls.Count;
            await Dispatch(app);
            mail.Calls.Count.Should().Be(before + 1, $"等了 {wait} 分後重試");
        }
        var row = (await Outbox(app)).Single();
        row.Status.Should().Be(AlertOutboxStatus.Failed);
        row.Attempts.Should().Be(5);
        row.LastError.Should().Be("auth:535");
        var channel = await Incident(app, CompleteAlertHandler.ChannelFingerprint);
        channel.Source.Should().Be(IncidentSource.Channel);
        channel.Category.Should().Be(IncidentCategory.Infra);
        channel.Severity.Should().Be(IncidentSeverity.Warning);
        channel.TitleKey.Should().Be(IncidentTitles.ChannelFailed);
        channel.TitleParamsJson.Should().Contain("auth").And.Contain("535");
        (await Outbox(app)).Should().NotContain(r => r.IncidentId == channel.Id, "通道失敗事件永遠不寄信");
        (await ChannelRow(app))!.ConsecutiveFailures.Should().Be(5);

        mail.Result = null;
        await Open(app, "fp-d02-next");
        await Dispatch(app);
        channel = await Incident(app, CompleteAlertHandler.ChannelFingerprint);
        channel.Status.Should().Be(IncidentStatus.Resolved);
        channel.ResolvedReason.Should().Be(IncidentResolvedReason.Recovered);
        (await ChannelRow(app))!.ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public async Task D03_HourlyCap3_FiveIncidents_ThreeSent_TwoDigested_DigestNextHourListsThem()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);
        SetTime(app, T0);
        await SetPolicy(app, PolicyCatalog.OpsAlertEmailMaxPerHour, "3");
        await Configure(app);
        var ids = new List<long>();
        for (var i = 0; i < 5; i++) ids.Add(await Open(app, $"fp-d03-{i}"));

        await Dispatch(app);
        mail.Sent.Should().HaveCount(3);
        var rows = await Outbox(app);
        rows.Count(r => r.Status == AlertOutboxStatus.Sent).Should().Be(3);
        var digested = rows.Where(r => r.Status == AlertOutboxStatus.Digested).ToList();
        digested.Should().HaveCount(2);

        // 同一個小時內不寄摘要信
        app.Time.Advance(TimeSpan.FromMinutes(20));
        await Dispatch(app);
        mail.Sent.Should().HaveCount(3);

        app.Time.Advance(TimeSpan.FromMinutes(31));
        await Dispatch(app);
        var digest = mail.Sent.Should().HaveCount(4).And.Subject.Last();
        digest.Subject.Should().Be("[Formwright] 過去一小時另有 2 則告警");
        foreach (var r in digested) digest.Body.Should().Contain($"#{r.IncidentId} ");
        rows = await Outbox(app);
        var digestRow = rows.Single(r => r.Transition == AlertTransition.Digest);
        digestRow.Status.Should().Be(AlertOutboxStatus.Sent);
        rows.Where(r => r.Status == AlertOutboxStatus.Digested).Should().OnlyContain(r => r.DigestOutboxId == digestRow.Id);
    }

    [Fact]
    public async Task D04_ConfigIncident_FactoryPolicy_NotificationOnly_NotQueued()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);
        await Configure(app);
        await Record(app, Infra("fp-d04", Now(app)) with { Source = IncidentSource.Server, Category = IncidentCategory.Config });

        (await Notifications(app)).Should().NotBeEmpty();
        (await Outbox(app)).Should().BeEmpty();
    }

    [Fact]
    public async Task D05_PasswordEncrypted_GetOnlyHasPassword_OtherKeysUnreadable_NullKeepsEmptyClears()
    {
        using var app = App(new FakeMailSender());
        await Configure(app);
        var row = (await ChannelRow(app))!;
        row.PasswordProtected.Should().NotBeNullOrEmpty().And.NotContain("s3cret-pw");
        var protector = app.Services.GetRequiredService<IAlertSecretProtector>();
        protector.TryUnprotect(row.PasswordProtected!).Should().Be("s3cret-pw");

        var get = await app.AsUser().GetFromJsonAsync<JsonElement>(Channel);
        get.GetProperty("hasPassword").GetBoolean().Should().BeTrue();
        get.GetProperty("passwordUnreadable").GetBoolean().Should().BeFalse();
        get.TryGetProperty("password", out _).Should().BeFalse();
        get.TryGetProperty("passwordProtected", out _).Should().BeFalse();
        get.GetRawText().Should().NotContain("s3cret-pw").And.NotContain(row.PasswordProtected!);
        get.GetProperty("editable").GetBoolean().Should().BeTrue();
        get.GetProperty("mailLocale").GetProperty("isFactory").GetBoolean().Should().BeTrue();

        // password 省略（null）＝不變
        await Configure(app, b => b.Remove("password"));
        (await ChannelRow(app))!.PasswordProtected.Should().Be(row.PasswordProtected);

        // 換一套金鑰加密的密碼：解不開
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            var tracked = await db.Set<AlertChannel>().SingleAsync();
            tracked.PasswordProtected = new EphemeralDataProtectionProvider().CreateProtector("ops-alert-smtp").Protect("other");
            await db.SaveChangesAsync();
        }
        get = await app.AsUser().GetFromJsonAsync<JsonElement>(Channel);
        get.GetProperty("hasPassword").GetBoolean().Should().BeTrue();
        get.GetProperty("passwordUnreadable").GetBoolean().Should().BeTrue();
        var test = await app.AsUser().PostAsync($"{Channel}/test", null);
        test.StatusCode.Should().Be(HttpStatusCode.Conflict, "密碼解不開＝設定不完整");
        (await Json(test)).GetProperty("code").GetString().Should().Be("smtpNotConfigured");

        // ""＝清除
        await Configure(app, b => b["password"] = "");
        (await ChannelRow(app))!.PasswordProtected.Should().BeNull();

        await using var audit = app.Services.CreateAsyncScope();
        var updates = await audit.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AuditLogEntry>().AsNoTracking()
            .Where(a => a.Action == "ops.channel.update").ToListAsync();
        updates.Should().HaveCount(3).And.OnlyContain(a => a.TargetKind == "channel" && a.TargetId == "smtp");
        updates.Should().OnlyContain(a => a.SummaryJson == null || !a.SummaryJson.Contains("s3cret-pw"));
    }

    private sealed class DemoSiteApp : TestApp
    {
        public DemoSiteApp() => Settings["Demo:LoginEnabled"] = "true";
    }

    [Fact]
    public async Task D06_DemoLoginEnabled_PutAndTest403()
    {
        using var app = new DemoSiteApp();
        (await app.AsUser().PutAsJsonAsync(Channel, Settings())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await app.AsUser().PostAsync($"{Channel}/test", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var get = await app.AsUser().GetFromJsonAsync<JsonElement>(Channel);
        get.GetProperty("editable").GetBoolean().Should().BeFalse();
        (await ChannelRow(app)).Should().BeNull();
    }

    [Fact]
    public async Task D07_ConnectFails_422StageConnect_NoExceptionText_Then409TooSoon()
    {
        // 真的 MailKit 寄送器連本機假伺服器：收下連線就中斷（埠在測試期間一直由它持有，不連外網）
        await using var server = LocalTcpServer.Resetting();
        using var app = new TestApp();
        await Configure(app, b =>
        {
            b["host"] = "127.0.0.1";
            b["port"] = server.Port;
        });
        var res = await app.AsUser().PostAsync($"{Channel}/test", null);
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await res.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;
        json.GetProperty("code").GetString().Should().Be("smtpTestFailed");
        json.GetProperty("params").GetProperty("stage").GetString().Should().Be("connect");
        body.Should().NotContain("Exception").And.NotContain("reset").And.NotContain("disconnected").And.NotContain("127.0.0.1");

        var again = await app.AsUser().PostAsync($"{Channel}/test", null);
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(again)).GetProperty("code").GetString().Should().Be("smtpTestTooSoon");

        app.Time.Advance(TimeSpan.FromSeconds(31));
        (await app.AsUser().PostAsync($"{Channel}/test", null)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "30 秒後可再寄");

        await using var scope = app.Services.CreateAsyncScope();
        var audits = await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AuditLogEntry>().AsNoTracking()
            .Where(a => a.Action == "ops.channel.test").ToListAsync();
        audits.Should().HaveCount(2).And.OnlyContain(a => a.Result == AuditResult.Failed && a.ReasonCode == "connect");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task D08_ConcurrentClaim_OneWins_ExpiredClaimReclaimed_StaleCompleteAffectsNothing(bool noLock)
    {
        var mail = new FakeMailSender();
        using var app = App(mail, noLock);
        SetTime(app, T0);
        await Configure(app);
        await Open(app, "fp-d08");

        var results = await Within(Task.WhenAll(
            Send(app, new ClaimAlertRequest(null, "A")), Send(app, new ClaimAlertRequest(null, "B"))));
        var claims = results.Select(r => ((AlertClaimResult)r.Data!).Claim).ToList();
        claims.Count(c => c is not null).Should().Be(1, "兩個寄送者同時領取只有一個成功");
        var winner = results[0].Data is AlertClaimResult { Claim: not null } ? "A" : "B";
        var claim = claims.Single(c => c is not null)!;
        claim.Attempts.Should().Be(1);
        // 領取者寄出後、記結果前「當掉」
        await app.Services.GetRequiredService<AlertDispatcher>().SendAsync(claim, CancellationToken.None);

        app.Time.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        await Dispatch(app);
        var row = (await Outbox(app)).Single();
        row.Status.Should().Be(AlertOutboxStatus.Sent);
        row.Attempts.Should().Be(2, "過期後被重領、次數加一");
        mail.Sent.Should().HaveCount(2, "至少一次：可能重複寄");

        var stale = await Send(app, new CompleteAlertRequest(claim.Id, winner, 1, MailSendResult.Success));
        stale.Data.Should().Be(false);
        app.Logs.Records.Should().Contain(r => r.Level == LogLevel.Warning && r.Category == typeof(CompleteAlertHandler).FullName);
        (await Outbox(app)).Single().Attempts.Should().Be(2);
    }

    [Fact]
    public async Task D09_DigestRetrying_SourceRowsKeptByUpkeep_PurgedAfterDigestSent()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);
        SetTime(app, T0);
        await SetPolicy(app, PolicyCatalog.OpsAlertEmailMaxPerHour, "3");
        await Configure(app);
        for (var i = 0; i < 5; i++) await Open(app, $"fp-d09-{i}");
        await Dispatch(app);

        // 跨整點：摘要信第一次寄送失敗
        mail.Result = m => m.Subject.Contains("過去一小時") ? new MailSendResult(false, MailSendResult.Send, 451) : MailSendResult.Success;
        app.Time.Advance(TimeSpan.FromMinutes(55));
        await Dispatch(app);
        var rows = await Outbox(app);
        var digest = rows.Single(r => r.Transition == AlertTransition.Digest);
        digest.Status.Should().Be(AlertOutboxStatus.Pending);
        digest.Attempts.Should().Be(1);
        rows.Where(r => r.Status == AlertOutboxStatus.Digested).Should().HaveCount(2).And.OnlyContain(r => r.DigestOutboxId == digest.Id);

        // 快轉超過保留天數（出廠 30 天）跑 ops.upkeep：摘要信還在重試，來源列不清；已寄的清掉
        var later = T0.AddDays(31);
        SetTime(app, later);
        var clock = new ManualClock(later);
        var runner = Runner(app, clock, app.Services.GetServices<IScheduledJob>().Where(j => j.Key == "ops.upkeep"));
        await runner.EnsureJobsAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(61));
        app.Time.Advance(TimeSpan.FromMinutes(61));
        await runner.TickAsync(CancellationToken.None);
        (await Row(app, "ops.upkeep")).LastStatus.Should().Be(ScheduledJobStatus.Succeeded);
        rows = await Outbox(app);
        rows.Should().HaveCount(3);
        rows.Where(r => r.Status == AlertOutboxStatus.Digested).Should().HaveCount(2);
        rows.Should().ContainSingle(r => r.Id == digest.Id);

        mail.Result = null;
        await Dispatch(app);
        (await Outbox(app)).Single(r => r.Id == digest.Id).Status.Should().Be(AlertOutboxStatus.Sent);
        clock.Advance(TimeSpan.FromHours(2));
        app.Time.Advance(TimeSpan.FromHours(2));
        await runner.TickAsync(CancellationToken.None);
        (await Outbox(app)).Should().BeEmpty("摘要信寄出後連同來源列一起清");

        await using var scope = app.Services.CreateAsyncScope();
        var purges = await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AuditLogEntry>().AsNoTracking()
            .Where(a => a.Action == "ops.retention.purge").Select(a => a.SummaryJson!).ToListAsync();
        purges.Should().Contain(s => s.Contains("\"alertOutbox\""));
    }

    [Fact]
    public async Task D10_NoneWithCredentials400_StartTlsAgainstServerWithoutStartTls_422Tls_NoPlaintextFallback()
    {
        await using var server = new NoStartTlsSmtpServer();
        using var app = new TestApp();
        var res = await app.AsUser().PutAsJsonAsync(Channel, Settings(b => b["security"] = "none"));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var bad = await Json(res);
        bad.GetProperty("code").GetString().Should().Be("smtpPlaintextAuth");
        bad.GetProperty("errors").GetRawText().Should().Contain(Application.Common.MessageText.Get("zh-TW", Application.Common.MessageText.SmtpPlaintextAuth));
        (await ChannelRow(app)).Should().BeNull();

        await Configure(app, b =>
        {
            b["host"] = "127.0.0.1";
            b["port"] = server.Port;
        });
        var test = await app.AsUser().PostAsync($"{Channel}/test", null);
        test.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Json(test)).GetProperty("params").GetProperty("stage").GetString().Should().Be("tls");
        server.Lines.Should().Contain(l => l.StartsWith("EHLO"));
        server.Lines.Should().NotContain(l => l.StartsWith("AUTH") || l.StartsWith("MAIL") || l.StartsWith("RCPT"), "沒有退回明文");
    }

    [Fact]
    public async Task MailKitSender_SecurityNoneRelay_SendsPlainTextMailToAllRecipients()
    {
        await using var server = new NoStartTlsSmtpServer();
        using var app = new TestApp();
        await Configure(app, b =>
        {
            b["host"] = "127.0.0.1";
            b["port"] = server.Port;
            b["security"] = "none";
            b["userName"] = null;
            b["password"] = null;
        });
        (await app.AsUser().PostAsync($"{Channel}/test", null)).EnsureSuccessStatusCode();
        server.Lines.Should().Contain(l => l.StartsWith("MAIL FROM:<ops@test.invalid>"));
        server.Lines.Should().Contain(l => l.StartsWith("RCPT TO:<a@test.invalid>")).And.Contain(l => l.StartsWith("RCPT TO:<b@test.invalid>"));
        server.Lines.Should().NotContain(l => l.StartsWith("AUTH"));
        server.Lines.Should().Contain(l => l.StartsWith("Subject:"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task D11_QuotaCountedUnderChannelLock_ConcurrentSenders_NeverOverCap(bool noLock)
    {
        // 第一組：已寄 2 封、另有 4 列到期，兩個寄送者同時領取
        var mail = new FakeMailSender();
        using (var app = App(mail, noLock))
        {
            SetTime(app, T0);
            await SetPolicy(app, PolicyCatalog.OpsAlertEmailMaxPerHour, "3");
            await Configure(app);
            await Open(app, "fp-d11-a");
            await Open(app, "fp-d11-b");
            await Dispatch(app);
            for (var i = 0; i < 4; i++) await Open(app, $"fp-d11-p{i}");

            var results = await Within(Task.WhenAll(
                Send(app, new ClaimAlertRequest(null, "X")), Send(app, new ClaimAlertRequest(null, "Y"))));
            results.Count(r => r.Data is AlertClaimResult { Claim: not null }).Should().Be(1, "額度在通道列鎖內數，不會兩個都看到「還差一封」");
            var rows = await Outbox(app);
            rows.Count(r => r.Status == AlertOutboxStatus.Digested).Should().Be(3);
            SentOrSendingWithinHour(rows, Now(app)).Should().Be(3);
        }

        // 第二組：X 已把 R1 領成 sending 時，Y 達上限而摘要化
        mail = new FakeMailSender();
        using (var app = App(mail, noLock))
        {
            SetTime(app, T0);
            await SetPolicy(app, PolicyCatalog.OpsAlertEmailMaxPerHour, "3");
            await Configure(app);
            await Open(app, "fp-d11-a");
            await Open(app, "fp-d11-b");
            await Dispatch(app);
            var r1Incident = await Open(app, "fp-d11-r1");
            var r2Incident = await Open(app, "fp-d11-r2");

            var x = ((AlertClaimResult)(await Send(app, new ClaimAlertRequest(null, "X"))).Data!).Claim!;
            (await Outbox(app)).Single(r => r.Id == x.Id).IncidentId.Should().Be(r1Incident);
            ((AlertClaimResult)(await Send(app, new ClaimAlertRequest(null, "Y"))).Data!).Claim.Should().BeNull();
            var rows = await Outbox(app);
            rows.Single(r => r.Id == x.Id).Status.Should().Be(AlertOutboxStatus.Sending, "已領取的不會被摘要化");
            rows.Single(r => r.IncidentId == r2Incident).Status.Should().Be(AlertOutboxStatus.Digested);
            SentOrSendingWithinHour(rows, Now(app)).Should().Be(3);

            (await Send(app, new CompleteAlertRequest(x.Id, "X", x.Attempts, MailSendResult.Success))).Data.Should().Be(true);
            (await Outbox(app)).Single(r => r.Id == x.Id).Status.Should().Be(AlertOutboxStatus.Sent);

            app.Time.Advance(TimeSpan.FromMinutes(55));
            await Dispatch(app);
            var digest = mail.Sent.Last();
            digest.Subject.Should().Be("[Formwright] 過去一小時另有 1 則告警");
            digest.Body.Should().Contain($"#{r2Incident} ").And.NotContain($"#{r1Incident} ");
        }
    }

    [Fact]
    public async Task D12_SixtyDigested_OneDigest_50LinesMax70Chars_BodyWithin4000_MoreCount()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);
        SetTime(app, T0);
        await SetPolicy(app, PolicyCatalog.OpsAlertEmailMaxPerHour, "1");
        await Configure(app);
        await Open(app, "fp-d12-first");
        for (var i = 0; i < 60; i++)
        {
            var route = i % 2 == 0 ? $"/api/r{i}" : $"/api/{new string('x', 80)}/{i}";
            await Record(app, Infra($"fp-d12-{i}", Now(app)) with
            {
                Source = IncidentSource.Server, Category = IncidentCategory.Defect, Code = "InvalidOperationException",
                TitleKey = IncidentTitles.ServerError, TitleParams = IncidentTitles.Server("System.InvalidOperationException", route, null),
            });
        }
        await Dispatch(app);
        (await Outbox(app)).Count(r => r.Status == AlertOutboxStatus.Digested).Should().Be(60);

        app.Time.Advance(TimeSpan.FromMinutes(55));
        await Dispatch(app);
        var digest = mail.Sent.Last();
        digest.Subject.Should().Be("[Formwright] 過去一小時另有 60 則告警");
        digest.Body.Length.Should().BeLessThanOrEqualTo(AlertOutbox.BodyMax);
        var lines = digest.Body.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith('#')).ToList();
        lines.Count.Should().BeLessThanOrEqualTo(50);
        lines.Should().OnlyContain(l => l.Length <= 70);
        lines.Should().Contain(l => l.EndsWith('…'));
        digest.Body.Should().EndWith($"另有 {60 - lines.Count} 則，請看監控頁。");
        var rows = await Outbox(app);
        var digestRow = rows.Single(r => r.Transition == AlertTransition.Digest);
        rows.Where(r => r.Status == AlertOutboxStatus.Digested).Should().HaveCount(60).And.OnlyContain(r => r.DigestOutboxId == digestRow.Id);
    }

    [Fact]
    public async Task D13_MailLocale_EnThenViThenFactory_SnapshotsKept_TestMailUsesCurrentLocale()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);
        SetTime(app, T0);
        await Configure(app, b => b["mailLocale"] = "en-US");
        await Open(app, "fp-d13-en");
        await Configure(app, b => b["mailLocale"] = "vi");
        await Open(app, "fp-d13-vi");
        await Configure(app, b => b["mailLocale"] = null);
        await Open(app, "fp-d13-zh");

        var rows = await Outbox(app);
        rows[0].Subject.Should().StartWith("[Formwright][Warning] Health check \"Free disk space\"");
        rows[0].Body.Should().Contain("Category: Infrastructure");
        rows[1].Subject.Should().StartWith("[Formwright][Cảnh báo]");
        rows[1].Body.Should().Contain("Loại: Hạ tầng");
        rows[2].Subject.Should().StartWith("[Formwright][警告] 健康檢查「");
        await Dispatch(app);
        mail.Sent.Select(m => m.Subject).Should().Equal(rows.Select(r => r.Subject), "寄的是寫入當時的快照");

        (await app.AsUser().PostAsync($"{Channel}/test", null)).EnsureSuccessStatusCode();
        mail.Sent.Last().Subject.Should().Be("[Formwright] 告警通道測試信");
        await Configure(app, b => b["mailLocale"] = "th");
        app.Time.Advance(TimeSpan.FromSeconds(31));
        var res = await app.AsUser().PostAsync($"{Channel}/test", null);
        res.EnsureSuccessStatusCode();
        (await Json(res)).GetProperty("sentTo").EnumerateArray().Select(e => e.GetString()).Should().Equal("a@test.invalid", "b@test.invalid");
        mail.Sent.Last().Subject.Should().Be("[Formwright] อีเมลทดสอบช่องทางแจ้งเตือน");
        (await app.AsUser().GetFromJsonAsync<JsonElement>(Channel)).GetProperty("mailLocale").GetProperty("value").GetString().Should().Be("th");
    }

    [Fact]
    public async Task ChannelDisabledOrUnconfigured_RowsSkipped_PendingSkippedAtClaim()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);
        await Open(app, "fp-skip-none");
        (await Outbox(app)).Single().Status.Should().Be(AlertOutboxStatus.Skipped, "沒設定通道照寫、標 skipped");

        await Configure(app);
        await Open(app, "fp-skip-later");
        await Configure(app, b => b["enabled"] = false);
        await Dispatch(app);
        mail.Calls.Should().BeEmpty();
        (await Outbox(app)).Should().OnlyContain(r => r.Status == AlertOutboxStatus.Skipped, "寫入後才停用：還沒寄的略過");

        var overview = await app.AsUser().GetFromJsonAsync<JsonElement>($"{Base}/overview");
        var channel = overview.GetProperty("channel");
        channel.GetProperty("configured").GetBoolean().Should().BeTrue();
        channel.GetProperty("enabled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task UpdateValidation_RequiredWhenEnabled_Formats_RecipientCount()
    {
        using var app = App(new FakeMailSender());
        async Task<JsonElement> Bad(Action<Dictionary<string, object?>> change)
        {
            var res = await app.AsUser().PutAsJsonAsync(Channel, Settings(change));
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            return await Json(res);
        }

        await Bad(b => b["host"] = "");
        await Bad(b => b["fromAddress"] = "not-an-email");
        await Bad(b => b["toAddresses"] = Array.Empty<string>());
        await Bad(b => b["toAddresses"] = Enumerable.Range(0, 21).Select(i => $"u{i}@test.invalid").ToArray());
        await Bad(b => b["toAddresses"] = new[] { "x@test.invalid", "bad" });
        await Bad(b => b["port"] = 0);
        await Bad(b => b["security"] = "tls13");
        await Bad(b => b["mailLocale"] = "ja");
        (await ChannelRow(app)).Should().BeNull();

        // 停用時可以存不完整的設定
        (await app.AsUser().PutAsJsonAsync(Channel, Settings(b =>
        {
            b["enabled"] = false;
            b["host"] = null;
            b["toAddresses"] = Array.Empty<string>();
        }))).EnsureSuccessStatusCode();
        (await app.AsUser("sales01").GetAsync(Channel)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await app.AsUser("sales01").PutAsJsonAsync(Channel, Settings())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var test = await app.AsUser().PostAsync($"{Channel}/test", null);
        test.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(test)).GetProperty("code").GetString().Should().Be("smtpNotConfigured");
    }

    [Fact]
    public async Task C13_SchedulerOff_DiskFull_DirectMailSentOnce_WithoutOpsMonitor_BackfillRowSentDirect()
    {
        var mail = new FakeMailSender();
        var full = new DiskFullSwitch();
        var disk = new FakeDiskSpace();
        using var app = new TestApp
        {
            ExtraServices = s =>
            {
                FakeDisk(s, disk);
                UseFakeMail(s, mail);
            },
        };
        app.Interceptors.Add(full);
        var watchdog = app.Services.GetRequiredService<OpsWatchdog>();
        var direct = app.Services.GetRequiredService<DirectAlertQueue>();
        await Configure(app);
        app.Time.Advance(TimeSpan.FromMinutes(5));
        await watchdog.TickAsync(CancellationToken.None);

        full.On = true;
        app.Time.Advance(TimeSpan.FromMinutes(5));
        await watchdog.TickAsync(CancellationToken.None);
        var engine = app.Services.GetRequiredService<HealthCheckEngine>();
        engine.Board[HealthCheckCatalog.DatabaseWritable].MessageCode.Should().Be("full");
        var transitionId = direct.PendingTransitionIds().Should().ContainSingle().Subject;

        await watchdog.SendDirectAsync(CancellationToken.None);
        await watchdog.SendDirectAsync(CancellationToken.None);
        var sent = mail.Sent.Should().ContainSingle("同一 TransitionId 只寄一次").Subject;
        sent.Subject.Should().StartWith("[Formwright][嚴重]").And.NotContain("#");
        sent.Body.Should().Contain("看門狗直接寄出");
        direct.SentUtc(transitionId).Should().NotBeNull();
        // 再跑一輪（仍然滿）：同一件事沒有新轉換，不再直送
        app.Time.Advance(TimeSpan.FromMinutes(5));
        await watchdog.TickAsync(CancellationToken.None);
        await watchdog.SendDirectAsync(CancellationToken.None);
        mail.Sent.Should().HaveCount(1);

        full.On = false;
        app.Time.Advance(TimeSpan.FromMinutes(1));
        await watchdog.TickAsync(CancellationToken.None);
        engine.PendingCount.Should().Be(0);
        var incident = await Incident(app, HealthFingerprint(HealthCheckCatalog.DatabaseWritable));
        var row = (await Outbox(app)).Single(r => r.IncidentId == incident.Id && r.Transition == AlertTransition.Opened);
        row.Status.Should().Be(AlertOutboxStatus.SentDirect);
        row.TransitionId.Should().Be(transitionId);
        await Dispatch(app);
        mail.Sent.Should().HaveCount(1, "補寫後沒有第二封");
    }

    private const string Disk = HealthCheckCatalog.StorageDiskFree, Writable = HealthCheckCatalog.DatabaseWritable;

    /// <summary>資料庫唯讀時跑一輪磁碟與資料庫可寫的嚴重檢查：直送佇列依序排進兩封開啟信（磁碟、資料庫可寫）。</summary>
    private static async Task<(HealthCheckEngine Engine, DirectAlertQueue Direct, string DiskId, string WritableId)> TwoDirectItems(
        TestApp app, ReadOnlySwitch readOnly)
    {
        await Configure(app);
        await EnsureChecks(app);
        var direct = app.Services.GetRequiredService<DirectAlertQueue>();
        var engine = Engine(app, new ScriptedProbe(Disk, new ProbeResult(HealthStatus.Critical, 500)),
            new ScriptedProbe(Writable, new ProbeResult(HealthStatus.Critical, null, "readOnly")));
        readOnly.On = true;
        foreach (var code in new[] { Disk, Writable })
        {
            var def = HealthCheckCatalog.Find(code)!;
            await engine.RunAsync(def, EffectiveHealthSettings.Of(def, null), null, CancellationToken.None);
            app.Time.Advance(TimeSpan.FromSeconds(1));
        }
        var ids = direct.PendingTransitionIds();
        ids.Should().HaveCount(2);
        return (engine, direct, ids[0], ids[1]);
    }

    /// <summary>
    /// 審查修正必修 1（直送與補寫交接）：直送正在寄磁碟那封（SMTP 在途）時資料庫恢復、補寫兩筆。
    /// 在途的那封佇列列由直送領著、寄完記 <c>sentDirect</c>；還沒開始寄的資料庫可寫那封交給佇列，直送不再從快照寄。每個轉換剛好寄一次。
    /// </summary>
    [Fact]
    public async Task DirectHandoff_BackfillWhileSmtpInFlight_InFlightRowSentDirect_QueuedOneLeftToOutbox_EachSentOnce()
    {
        var mail = new FakeMailSender();
        var readOnly = new ReadOnlySwitch();
        using var app = App(mail);
        app.Interceptors.Add(readOnly);
        var (engine, direct, diskId, writableId) = await TwoDirectItems(app, readOnly);

        var (entered, release) = mail.HoldFirst();
        var sending = direct.SendDueAsync(CancellationToken.None);
        await Within(entered);
        readOnly.On = false;
        await engine.FlushAsync(CancellationToken.None);
        engine.PendingCount.Should().Be(0);
        var rows = await Outbox(app);
        rows.Single(r => r.TransitionId == diskId).Status.Should().Be(AlertOutboxStatus.Sending, "在途：佇列列由直送領著，佇列不會再寄");
        rows.Single(r => r.TransitionId == writableId).Status.Should().Be(AlertOutboxStatus.Pending);
        release.SetResult();
        await Within(sending);

        rows = await Outbox(app);
        var diskRow = rows.Single(r => r.TransitionId == diskId);
        diskRow.Status.Should().Be(AlertOutboxStatus.SentDirect);
        diskRow.SentUtc.Should().NotBeNull();
        diskRow.ClaimedBy.Should().BeNull();
        await Dispatch(app);
        await direct.SendDueAsync(CancellationToken.None);
        await Dispatch(app);
        mail.Sent.Should().HaveCount(2, "磁碟由直送、資料庫可寫由佇列，各寄一次");
        mail.Sent.Should().ContainSingle(m => m.Body.Contains("看門狗直接寄出"));
        (await Outbox(app)).Single(r => r.TransitionId == writableId).Status.Should().Be(AlertOutboxStatus.Sent);
        direct.PendingTransitionIds().Should().BeEmpty();
    }

    /// <summary>審查修正必修 1（回到正常撤回）：直送正在寄第一封時，第二封的檢查回到正常；撤回的那封不再從快照寄出。</summary>
    [Fact]
    public async Task DirectWithdraw_BackToNormalWhileSendingAnother_WithdrawnNotSent()
    {
        var mail = new FakeMailSender();
        var readOnly = new ReadOnlySwitch();
        using var app = App(mail);
        app.Interceptors.Add(readOnly);
        var (_, direct, _, writableId) = await TwoDirectItems(app, readOnly);

        var (entered, release) = mail.HoldFirst();
        var sending = direct.SendDueAsync(CancellationToken.None);
        await Within(entered);
        direct.Consider(new HealthObservation(Writable, Guid.NewGuid().ToString("N"), Now(app), HealthStatus.Ok, null, null, 1, "x"),
            HealthStatus.Critical);
        release.SetResult();
        await Within(sending);

        mail.Sent.Should().ContainSingle("資料庫可寫已回到正常，它的開啟信撤回");
        direct.SentUtc(writableId).Should().BeNull();
        direct.PendingTransitionIds().Should().BeEmpty();
    }

    /// <summary>
    /// 交接協定的回滾（審查修正必修 1 的保護）：補寫寫完卻沒提交（交易回滾）時，直送的信先保留不寄；
    /// 過了交接寬限、資料庫查不到這筆結果，直送收回來照寄；之後補寫成功記 <c>sentDirect</c>，總共寄一次。
    /// </summary>
    [Fact]
    public async Task DirectHandoff_BackfillRolledBack_DirectResumesAfterGrace_SentOnce()
    {
        var mail = new FakeMailSender();
        var readOnly = new ReadOnlySwitch();
        var rollback = new ThrowBeforeCommit();
        using var app = App(mail);
        app.Interceptors.Add(readOnly);
        app.Interceptors.Add(rollback);
        var (engine, direct, diskId, _) = await TwoDirectItems(app, readOnly);

        readOnly.On = false;
        rollback.Armed = true;
        await engine.FlushAsync(CancellationToken.None);
        engine.PendingCount.Should().Be(2, "第一筆沒提交，停在那一筆");
        (await Results(app, Disk)).Should().BeEmpty();
        readOnly.On = true;
        await direct.SendDueAsync(CancellationToken.None);
        mail.Sent.Should().ContainSingle("磁碟那封交接中、先不寄；資料庫可寫那封照寄");

        app.Time.Advance(DirectAlertQueue.HandoffGrace);
        await direct.SendDueAsync(CancellationToken.None);
        mail.Sent.Should().HaveCount(2, "寬限過後查不到結果：交易沒提交，直送收回照寄");
        direct.SentUtc(diskId).Should().NotBeNull();

        readOnly.On = false;
        await engine.FlushAsync(CancellationToken.None);
        engine.PendingCount.Should().Be(0);
        (await Outbox(app)).Should().OnlyContain(r => r.Status == AlertOutboxStatus.SentDirect);
        await Dispatch(app);
        mail.Sent.Should().HaveCount(2);
    }

    /// <summary>
    /// 交接 fallback 跟著最新一次補寫（codex 短核對 P2）：SMTP 在途時補寫以 <c>pending</c> 保留後回滾；同一檢查回到正常，
    /// 再次補寫判定已過時（<c>skipped</c>）並提交；之後 SMTP 失敗，交回的佇列列是 <c>skipped</c>、過時的開啟信不會再寄。
    /// <c>reconcile</c>：第二次補寫提交後回傳失敗，重試走去重核對（<see cref="AlertOutboxWriter.ReconcileBackfillAsync"/>），也不能把交接改回 <c>pending</c>。
    /// </summary>
    [Theory]
    [InlineData("backfill")]
    [InlineData("reconcile")]
    public async Task DirectHandoff_RolledBackThenRecovered_RetryBackfillSuperseded_SmtpFails_RowSkipped_StaleMailNotSent(string path)
    {
        var mail = new FakeMailSender();
        var readOnly = new ReadOnlySwitch();
        var rollback = new ThrowBeforeCommit();
        var afterCommit = new ThrowAfterCommit();
        using var app = App(mail);
        app.Interceptors.Add(readOnly);
        app.Interceptors.Add(rollback);
        app.Interceptors.Add(afterCommit);
        await Configure(app);
        await EnsureChecks(app);
        var direct = app.Services.GetRequiredService<DirectAlertQueue>();
        var def = HealthCheckCatalog.Find(Disk)!;
        var engine = Engine(app, new ScriptedProbe(Disk, new ProbeResult(HealthStatus.Critical, 500), new ProbeResult(HealthStatus.Ok, 50_000)));
        readOnly.On = true;
        await engine.RunAsync(def, EffectiveHealthSettings.Of(def, null), null, CancellationToken.None);
        var diskId = direct.PendingTransitionIds().Should().ContainSingle().Subject;

        var (entered, release) = mail.HoldFirst();
        mail.Result = m => m.Body.Contains("看門狗直接寄出") ? new MailSendResult(false, MailSendResult.Send) : MailSendResult.Success;
        var sending = direct.SendDueAsync(CancellationToken.None);
        await Within(entered);
        // 1. SMTP 在途，補寫以 pending 保留，交易回滾
        readOnly.On = false;
        rollback.Armed = true;
        await engine.FlushAsync(CancellationToken.None);
        engine.PendingCount.Should().Be(1);
        // 2. 同一檢查回到正常（資料庫仍寫不進），寬限內再次補寫：這次判定已過時
        readOnly.On = true;
        app.Time.Advance(TimeSpan.FromMinutes(1));
        await engine.RunAsync(def, EffectiveHealthSettings.Of(def, null), null, CancellationToken.None);
        engine.PendingCount.Should().Be(2);
        readOnly.On = false;
        if (path == "reconcile")
        {
            afterCommit.Armed = 1;
            await engine.FlushAsync(CancellationToken.None);
            engine.PendingCount.Should().Be(2, "提交後丟例外：執行器以為沒寫入");
        }
        await engine.FlushAsync(CancellationToken.None);
        engine.PendingCount.Should().Be(0);
        var opened = (await Outbox(app)).Single(r => r.TransitionId == diskId);
        opened.Status.Should().Be(AlertOutboxStatus.Sending, "在途：佇列列由直送領著");
        // 3. SMTP 失敗：交回的是這一次補寫的判定
        release.SetResult();
        await Within(sending);
        await direct.SendDueAsync(CancellationToken.None);

        opened = (await Outbox(app)).Single(r => r.TransitionId == diskId);
        opened.Status.Should().Be(AlertOutboxStatus.Skipped, "已過時的開啟信交回 skipped，不是第一次保留時的 pending");
        opened.ClaimedBy.Should().BeNull();
        await Dispatch(app);
        mail.Sent.Should().NotContain(m => m.Subject == opened.Subject, "過時的開啟信不會被佇列寄出");
        direct.PendingTransitionIds().Should().BeEmpty();
    }

    /// <summary>審查修正必修 3：收件信箱陣列含 null 回 400（<c>toAddresses</c> 欄位錯誤），不是 500；啟用、停用都一樣。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateValidation_NullRecipient_400ToAddresses(bool enabled)
    {
        using var app = App(new FakeMailSender());
        var res = await app.AsUser().PutAsJsonAsync(Channel, Settings(b =>
        {
            b["enabled"] = enabled;
            b["toAddresses"] = new[] { "a@test.invalid", null };
        }));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        (await Json(res)).GetProperty("errors").TryGetProperty("toAddresses", out _).Should().BeTrue();
        (await ChannelRow(app)).Should().BeNull();
    }

    /// <summary>
    /// 審查修正必修 4（§17.13 偏離 1）：整點前建立、整點後才被摘要化的列等下一個整點。01:50 建立、02:05 額度滿而摘要化，
    /// 02:06 本小時雖然還沒有摘要信也不寄；03:00 之後的第一輪才寄摘要信。
    /// </summary>
    [Fact]
    public async Task D03b_CreatedBeforeHour_DigestedAfterHour_WaitsForNextHour()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);
        SetTime(app, Start.AddMinutes(50));
        await SetPolicy(app, PolicyCatalog.OpsAlertEmailMaxPerHour, "1");
        await Configure(app);
        await Open(app, "fp-d03b-1");
        await Open(app, "fp-d03b-2");

        SetTime(app, Start.AddMinutes(65));
        await Dispatch(app);
        mail.Sent.Should().ContainSingle();
        (await Outbox(app)).Should().ContainSingle(r => r.Status == AlertOutboxStatus.Digested);

        SetTime(app, Start.AddMinutes(66));
        await Dispatch(app);
        SetTime(app, Start.AddMinutes(119));
        await Dispatch(app);
        mail.Sent.Should().ContainSingle("02:05 才摘要化，等 03:00");

        SetTime(app, Start.AddMinutes(121));
        await Dispatch(app);
        mail.Sent.Should().HaveCount(2);
        mail.Sent.Last().Subject.Should().Be("[Formwright] 過去一小時另有 1 則告警");
    }

    /// <summary>審查建議（直送停用語意）：直送成功讀到通道已停用，還沒寄的信丟掉（同佇列 pending → skipped）；之後重新啟用不補寄。</summary>
    [Fact]
    public async Task DirectQueue_ChannelDisabledRead_PendingDropped_NotSentAfterReEnable()
    {
        var mail = new FakeMailSender();
        var readOnly = new ReadOnlySwitch();
        using var app = App(mail);
        app.Interceptors.Add(readOnly);
        var (_, direct, _, _) = await TwoDirectItems(app, readOnly);

        readOnly.On = false;
        await Configure(app, b => b["enabled"] = false);
        await direct.SendDueAsync(CancellationToken.None);
        direct.PendingTransitionIds().Should().BeEmpty();
        await Configure(app);
        await direct.SendDueAsync(CancellationToken.None);
        mail.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// 審查建議（安全測試）：收件地址、寄件地址帶 CRLF 回 400；寄件名稱與主旨帶 CRLF 時，真的 MailKit 寄到本機假 relay，
    /// 信件標頭沒有多出注入的 <c>Bcc:</c>／<c>X-Injected:</c>，也沒有多寄給注入的收件人。
    /// </summary>
    [Fact]
    public async Task MailKitSender_CrlfInRecipientFromNameSubject_RejectedOrEncoded_NoHeaderInjection()
    {
        await using var server = new NoStartTlsSmtpServer();
        using var app = new TestApp();
        const string inject = "\r\nBcc: evil@test.invalid\r\nX-Injected: 1";
        foreach (var change in new Action<Dictionary<string, object?>>[]
        {
            b => b["toAddresses"] = new[] { "a@test.invalid" + inject },
            b => b["fromAddress"] = "ops@test.invalid" + inject,
        })
        {
            var res = await app.AsUser().PutAsJsonAsync(Channel, Settings(change));
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        var settings = new SmtpSettings("127.0.0.1", server.Port, AlertSecurity.None, null, null, "ops@test.invalid", "Ops" + inject,
            ["a@test.invalid"], null);
        var sender = app.Services.GetRequiredService<IAlertMailSender>();
        var result = await sender.SendAsync(settings, null, new AlertMail("[Formwright] disk" + inject, "body"), CancellationToken.None);
        result.Ok.Should().BeTrue();
        server.Lines.Should().NotContain(l => l.StartsWith("Bcc:", StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("X-Injected:", StringComparison.OrdinalIgnoreCase));
        server.Lines.Where(l => l.StartsWith("RCPT TO:")).Should().Equal("RCPT TO:<a@test.invalid>");
        server.Lines.Should().ContainSingle(l => l.StartsWith("Subject:"));
    }

    /// <summary>審查建議（安全測試）：<c>sslOnConnect</c> 連到出示自簽（不受信任）憑證的本機伺服器，連線失敗歸 <c>tls</c>，沒有略過憑證驗證。</summary>
    [Fact]
    public async Task MailKitSender_UntrustedCertificate_422StageTls()
    {
        await using var server = LocalTcpServer.SelfSignedTls();
        // 對照：接受任何憑證的用戶端握得成功（伺服器確實出示了憑證，失敗只因為不受信任）
        using (var probe = new System.Net.Sockets.TcpClient())
        {
            await probe.ConnectAsync(System.Net.IPAddress.Loopback, server.Port);
            await using var tls = new System.Net.Security.SslStream(probe.GetStream(), false, (_, _, _, _) => true);
            await Within(tls.AuthenticateAsClientAsync("127.0.0.1"));
            tls.RemoteCertificate.Should().NotBeNull();
        }
        using var app = new TestApp();
        await Configure(app, b =>
        {
            b["host"] = "127.0.0.1";
            b["port"] = server.Port;
            b["security"] = "sslOnConnect";
        });
        var res = await app.AsUser().PostAsync($"{Channel}/test", null);
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Json(res)).GetProperty("params").GetProperty("stage").GetString().Should().Be("tls");
    }
}
