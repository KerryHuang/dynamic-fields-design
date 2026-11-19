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
        // 真的 MailKit 寄送器連一個沒人聽的本機埠（不連外網）
        using var app = new TestApp();
        await Configure(app, b =>
        {
            b["host"] = "127.0.0.1";
            b["port"] = ClosedPort();
        });
        var res = await app.AsUser().PostAsync($"{Channel}/test", null);
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await res.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;
        json.GetProperty("code").GetString().Should().Be("smtpTestFailed");
        json.GetProperty("params").GetProperty("stage").GetString().Should().Be("connect");
        body.Should().NotContain("Exception").And.NotContain("refused").And.NotContain("127.0.0.1");

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
}
