using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Application.Dynamic.OpsService.Scheduling;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Ops;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static DynamicModules.WebAPITests.Ops.OpsIncidentKit;
using static DynamicModules.WebAPITests.Ops.SchedulerTestKit;

namespace DynamicModules.WebAPITests.Ops;

/// <summary>維運事件（維運設計 §17.2.2–§17.2.5、§17.8 B01–B09）。</summary>
[Trait("Category", "Postgres")]
public class IncidentTests
{
    private static async Task<int> AdminCount(TestApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AppUser>().CountAsync(u => u.IsActive && u.IsSystemAdmin);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task B01_TwoInstances_SameFingerprint_OneIncidentCountSix_NotifiedOnce(bool noLock)
    {
        using var app = new TestApp { WithoutWriteSerialization = noLock };
        var (a, drainA) = Inbox(app);
        var (b, drainB) = Inbox(app);
        var now = app.Time.GetUtcNow().UtcDateTime;
        for (var i = 0; i < 3; i++)
        {
            a.Post(Defect("fp-b01", now.AddSeconds(i)));
            b.Post(Defect("fp-b01", now.AddSeconds(i)));
        }
        a.Count.Should().Be(1, "同指紋在收件匣裡合併");

        await Within(Task.WhenAll(drainA.FlushAsync(CancellationToken.None), drainB.FlushAsync(CancellationToken.None)));

        var incident = (await Incidents(app)).Should().ContainSingle().Subject;
        incident.OccurrenceCount.Should().Be(6);
        incident.Status.Should().Be(IncidentStatus.Open);
        (await Notifications(app)).Should().HaveCount(await AdminCount(app), "只在開啟時通知一次（每位管理員一則）");
    }

    [Fact]
    public async Task B02_ResolvedThenSeenAgain_ReopensAsRegression_NotifiesAgain()
    {
        using var app = new TestApp();
        var now = app.Time.GetUtcNow().UtcDateTime;
        var id = (await Record(app, Defect("fp-b02", now))).IncidentId!.Value;
        var first = (await Notifications(app)).Count;

        var res = await app.AsUser().PostAsJsonAsync($"{Base}/incidents/{id}/resolve", new { expectedStatus = "open", note = "已修正" });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        (await Incident(app, "fp-b02")).ResolvedReason.Should().Be(IncidentResolvedReason.Manual);

        (await Record(app, Defect("fp-b02", now.AddMinutes(1)))).Transition.Should().Be(IncidentTransition.Regressed);
        var reopened = await Incident(app, "fp-b02");
        reopened.Id.Should().Be(id);
        reopened.Status.Should().Be(IncidentStatus.Open);
        reopened.Regression.Should().BeTrue();
        reopened.ReopenCount.Should().Be(1);
        (await Notifications(app)).Should().HaveCount(first * 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task B03_TwoAdminsAcknowledgeAtOnce_OneWins_Other409(bool noLock)
    {
        using var app = new TestApp { WithoutWriteSerialization = noLock };
        (await app.AsUser().PutAsJsonAsync("/api/v3/dynamic/admin/users/sales01",
            new { displayName = "業務一", groupIds = Array.Empty<long>(), isSystemAdmin = true })).EnsureSuccessStatusCode();
        var id = (await Record(app, Defect("fp-b03", app.Time.GetUtcNow().UtcDateTime))).IncidentId!.Value;

        var body = new { expectedStatus = "open" };
        var responses = await Within(Task.WhenAll(
            app.AsUser().PostAsJsonAsync($"{Base}/incidents/{id}/acknowledge", body),
            app.AsUser("sales01").PostAsJsonAsync($"{Base}/incidents/{id}/acknowledge", body)));

        responses.Select(r => r.StatusCode).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        var conflict = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("incidentStateChanged");
        var row = await Incident(app, "fp-b03");
        row.Status.Should().Be(IncidentStatus.Acknowledged);
        row.AcknowledgedBy.Should().BeOneOf("admin", "sales01");
    }

    [Fact]
    public async Task B04_HealthIncident_CannotBeResolvedManually_NonAdmin403()
    {
        using var app = new TestApp();
        var signal = Defect(HealthFingerprint("storage.diskFree"), app.Time.GetUtcNow().UtcDateTime) with
        {
            Source = IncidentSource.Health, Category = IncidentCategory.Infra, Code = "storage.diskFree", TitleKey = IncidentTitles.HealthPrefix + "storage.diskFree",
        };
        var id = (await Record(app, signal)).IncidentId!.Value;

        var res = await app.AsUser().PostAsJsonAsync($"{Base}/incidents/{id}/resolve", new { expectedStatus = "open" });
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("incidentManagedBySource");
        (await app.AsUser().GetFromJsonAsync<JsonElement>($"{Base}/incidents/{id}")).GetProperty("manualResolvable").GetBoolean().Should().BeFalse();
        (await app.AsUser("sales01").GetAsync($"{Base}/incidents/{id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await app.AsUser("sales01").PostAsJsonAsync($"{Base}/incidents/{id}/acknowledge", new { expectedStatus = "open" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task B05_Upkeep_ExpiresQuietDefects_RenotifiesUnacknowledgedCritical()
    {
        using var app = new TestApp();
        var clock = new ManualClock(Start);
        // 出廠：自動結案 72 小時、重複提醒 24 小時；upkeep 會在 Start+61 分跑
        SetTime(app, Start.AddHours(-80));
        await Record(app, Defect("fp-quiet", Start.UtcDateTime.AddHours(-80)));
        SetTime(app, Start.AddHours(-30));
        await Record(app, Defect("fp-critical", Start.UtcDateTime.AddHours(-30), IncidentSeverity.Critical));
        SetTime(app, Start.AddHours(-1));
        await Record(app, Defect("fp-recent", Start.UtcDateTime.AddHours(-1)));
        var before = (await Notifications(app)).Count;

        var runner = Runner(app, clock, app.Services.GetServices<IScheduledJob>().Where(j => j.Key == "ops.upkeep"));
        await runner.EnsureJobsAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(61));
        await runner.TickAsync(CancellationToken.None);

        (await Row(app, "ops.upkeep")).LastStatus.Should().Be(ScheduledJobStatus.Succeeded);
        var quiet = await Incident(app, "fp-quiet");
        quiet.Status.Should().Be(IncidentStatus.Resolved);
        quiet.ResolvedReason.Should().Be(IncidentResolvedReason.Expired);
        (await Incident(app, "fp-recent")).Status.Should().Be(IncidentStatus.Open);
        var critical = await Incident(app, "fp-critical");
        critical.Status.Should().Be(IncidentStatus.Open, "30 小時前才出現，還沒到 72 小時");
        var admins = await AdminCount(app);
        (await Notifications(app)).Should().HaveCount(before + admins, "嚴重未確認、超過 24 小時，再提醒一次");

        // 再跑一次（一小時後）：剛提醒過，不再發
        clock.Advance(TimeSpan.FromMinutes(61));
        await runner.TickAsync(CancellationToken.None);
        (await Notifications(app)).Should().HaveCount(before + admins);
    }

    [Fact]
    public async Task B06_UnhandledException_And_ClientError_BecomeIncidents()
    {
        using var app = new OpsTestApp();
        (await app.AsUser("sales01").GetAsync("/api/v3/dynamic/test-ops/throw?message=boom%2042")).StatusCode
            .Should().Be(HttpStatusCode.InternalServerError);
        (await Incidents(app)).Should().BeEmpty("請求裡只放進記憶體收件匣，不寫資料庫");
        await app.Services.GetRequiredService<IncidentInboxDrain>().FlushAsync(CancellationToken.None);

        var server = (await Incidents(app)).Should().ContainSingle().Subject;
        server.Source.Should().Be(IncidentSource.Server);
        server.Category.Should().Be(IncidentCategory.Defect);
        server.Severity.Should().Be(IncidentSeverity.Warning);
        server.TitleKey.Should().Be(IncidentTitles.ServerError);
        var sample = (await Samples(app, server.Id)).Should().ContainSingle().Subject;
        sample.RawMessage.Should().Be("boom 42");
        sample.UserId.Should().Be("sales01");
        sample.RawStack.Should().Contain("InvalidOperationException");
        var notified = (await Notifications(app)).Count;
        notified.Should().Be(await AdminCount(app));

        (await app.AsUser("sales01").PostAsJsonAsync("/api/v3/dynamic/ops/client-errors", new Dictionary<string, object?>
        {
            ["kind"] = "vue", ["name"] = "TypeError", ["message"] = "x is 3", ["route"] = "/dynamic/crm", ["moduleKey"] = "crm",
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await app.Services.GetRequiredService<IncidentInboxDrain>().FlushAsync(CancellationToken.None);
        var client = (await Incidents(app)).Single(i => i.Source == IncidentSource.Client);
        client.Category.Should().Be(IncidentCategory.Defect);
        client.Severity.Should().Be(IncidentSeverity.Info);
        (await Samples(app, client.Id)).Should().ContainSingle().Which.RawMessage.Should().Be("x is 3");
        (await Notifications(app)).Should().HaveCount(notified, "資訊級不發通知");
    }

    [Fact]
    public async Task B07_TwentySamplesKept_OldestTrimmed()
    {
        using var app = new TestApp();
        var now = app.Time.GetUtcNow().UtcDateTime;
        long id = 0;
        for (var i = 0; i < 21; i++)
            id = (await Record(app, Defect("fp-b07", now.AddSeconds(i),
                sample: new IncidentSampleData(now.AddSeconds(i), $"req{i:D2}", "sales01", "test", "/api/x", RawMessage: $"m{i}")))).IncidentId!.Value;

        var samples = await Samples(app, id);
        samples.Should().HaveCount(OpsIncidentSample.MaxPerIncident);
        samples.Select(s => s.RequestId).Should().NotContain("req00").And.Contain("req20");
        (await Incident(app, "fp-b07")).OccurrenceCount.Should().Be(21);
    }

    [Fact]
    public async Task B08_AdminGrants_OneSecurityIncident_EachChangeNotified_SamplesPointToAudit()
    {
        using var app = new TestApp();
        async Task Grant(string user) => (await app.AsUser().PutAsJsonAsync($"/api/v3/dynamic/admin/users/{user}",
            new { displayName = user, groupIds = Array.Empty<long>(), isSystemAdmin = true })).EnsureSuccessStatusCode();

        await Grant("sales01");
        var incident = (await Incidents(app)).Should().ContainSingle().Subject;
        incident.Category.Should().Be(IncidentCategory.Security);
        incident.Fingerprint.Should().Be(ErrorFingerprint.ForInfra("security", SecurityIncidents.AdminChangedCode));
        (await app.AsUser().PostAsJsonAsync($"{Base}/incidents/{incident.Id}/acknowledge", new { expectedStatus = "open" })).EnsureSuccessStatusCode();
        await Grant("sales02");

        incident = (await Incidents(app)).Should().ContainSingle().Subject;
        incident.OccurrenceCount.Should().Be(2);
        var forAdmin = (await Notifications(app)).Where(n => n.UserId == "admin").ToList();
        forAdmin.Should().HaveCount(2, "安全類每次都通知，確認之後也一樣");
        forAdmin.Should().OnlyContain(n => n.Link.StartsWith("/system/audit?id="));

        var samples = await Samples(app, incident.Id);
        samples.Should().HaveCount(2);
        await using var scope = app.Services.CreateAsyncScope();
        var audits = await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AuditLogEntry>().AsNoTracking()
            .Where(a => a.Action == "user.update").OrderBy(a => a.Id).ToListAsync();
        samples.Select(s => s.AuditLogId).Should().Equal(audits.Select(a => (long?)a.Id));
        audits.Select(a => a.TargetId).Should().Equal("sales01", "sales02");
        samples.Should().OnlyContain(s => s.RawMessage == null && s.UserId == "admin");
    }

    [Fact]
    public async Task B09_SensitiveText_KeptInSampleOnly_NotInIncidentOrNotification()
    {
        using var app = new OpsTestApp();
        const string message = "客戶 王小明 token=abc123 fwp_0123456789abcdef 無法出貨";
        (await app.AsUser().GetAsync($"/api/v3/dynamic/test-ops/throw?message={Uri.EscapeDataString(message)}")).StatusCode
            .Should().Be(HttpStatusCode.InternalServerError);
        await app.Services.GetRequiredService<IncidentInboxDrain>().FlushAsync(CancellationToken.None);

        var incident = (await Incidents(app)).Should().ContainSingle().Subject;
        (await Samples(app, incident.Id)).Single().RawMessage.Should().Be(message);
        incident.NormalizedMessage.Should().NotContain("abc123").And.NotContain("fwp_0123");
        var notes = await Notifications(app);
        notes.Should().NotBeEmpty();
        foreach (var n in notes)
            foreach (var text in new[] { n.TitleSnapshot, n.DataJson ?? "" })
                foreach (var secret in new[] { "王小明", "abc123", "fwp_", "無法出貨" })
                    text.Should().NotContain(secret);
    }

    [Fact]
    public async Task IncidentList_FiltersAndCursor_DetailHasSamplesAndHistory()
    {
        using var app = new TestApp();
        var now = app.Time.GetUtcNow().UtcDateTime;
        for (var i = 0; i < 5; i++)
            await Record(app, Defect($"fp-list-{i}", now.AddSeconds(i), i % 2 == 0 ? IncidentSeverity.Warning : IncidentSeverity.Critical,
                new IncidentSampleData(now, $"r{i}", "sales01", "test", "/api/x", RawMessage: "m")));
        var first = await app.AsUser().GetFromJsonAsync<JsonElement>($"{Base}/incidents?status=open&limit=2");
        var items = first.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(2);
        var cursor = first.GetProperty("nextCursor").GetString();
        var second = await app.AsUser().GetFromJsonAsync<JsonElement>($"{Base}/incidents?status=open&limit=10&cursor={cursor}");
        var ids = items.Concat(second.GetProperty("items").EnumerateArray()).Select(e => e.GetProperty("id").GetInt64()).ToList();
        ids.Should().HaveCount(5).And.OnlyHaveUniqueItems().And.BeInDescendingOrder();
        (await app.AsUser().GetFromJsonAsync<JsonElement>($"{Base}/incidents?severity=critical")).GetProperty("items").GetArrayLength().Should().Be(2);
        (await app.AsUser().GetAsync($"{Base}/incidents?status=bogus")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var id = ids[0];
        (await app.AsUser().PostAsJsonAsync($"{Base}/incidents/{id}/acknowledge", new { expectedStatus = "open" })).EnsureSuccessStatusCode();
        (await app.AsUser().PostAsJsonAsync($"{Base}/incidents/{id}/resolve", new { expectedStatus = "acknowledged", note = "處理完" }))
            .EnsureSuccessStatusCode();
        var detail = await app.AsUser().GetFromJsonAsync<JsonElement>($"{Base}/incidents/{id}");
        detail.GetProperty("status").GetString().Should().Be("resolved");
        detail.GetProperty("handler").GetString().Should().Be("contactVendor");
        detail.GetProperty("samples").GetArrayLength().Should().Be(1);
        detail.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("action").GetString())
            .Should().BeEquivalentTo("incident.acknowledge", "incident.resolve");
        (await app.AsUser().GetAsync($"{Base}/incidents/999999")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await app.AsUser().PostAsJsonAsync($"{Base}/incidents/{id}/resolve", new { expectedStatus = "open" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task F01_Upkeep_PurgesResolvedIncidentsSamplesAndHealthResults_KeepsOpen()
    {
        using var app = new TestApp();
        var clock = new ManualClock(Start);
        var old = Start.UtcDateTime.AddDays(-200);
        SetTime(app, Start.AddDays(-200));
        var oldResolved = (await Record(app, Defect("fp-old-resolved", old,
            sample: new IncidentSampleData(old, "r1", null, "test", null, RawMessage: "m")))).IncidentId!.Value;
        await Record(app, Defect("fp-old-resolved", old) with { Recovered = true });
        var oldOpen = (await Record(app, Defect("fp-old-open", old, IncidentSeverity.Critical,
            new IncidentSampleData(old, "r2", null, "test", null, RawMessage: "m")))).IncidentId!.Value;
        await EnsureChecks(app);
        await Send(app, new Application.Dynamic.OpsService.Health.RecordHealthResultRequest(
            Observation("database.integrity", old, HealthStatus.Ok, 0, "A"), null));
        SetTime(app, Start);
        await Send(app, new Application.Dynamic.OpsService.Health.RecordHealthResultRequest(
            Observation("database.integrity", Start.UtcDateTime.AddMinutes(-5), HealthStatus.Ok, 0, "A"), null));
        // 未結事件的舊樣本照樣本天數（14 天）清；已結事件超過 180 天整件刪
        var runner = Runner(app, clock, app.Services.GetServices<IScheduledJob>().Where(j => j.Key == "ops.upkeep"));
        await runner.EnsureJobsAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(61));
        await runner.TickAsync(CancellationToken.None);

        (await Row(app, "ops.upkeep")).LastStatus.Should().Be(ScheduledJobStatus.Succeeded);
        var left = await Incidents(app);
        left.Select(i => i.Id).Should().NotContain(oldResolved);
        left.Should().Contain(i => i.Id == oldOpen, "未結事件不受事件保留天數影響");
        (await Samples(app, oldOpen)).Should().BeEmpty();
        (await Results(app, "database.integrity")).Should().ContainSingle().Which.CheckedUtc.Should().BeAfter(Start.UtcDateTime.AddDays(-1));

        await using var scope = app.Services.CreateAsyncScope();
        var purges = await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AuditLogEntry>().AsNoTracking()
            .Where(a => a.Action == "ops.retention.purge").Select(a => a.SummaryJson!).ToListAsync();
        purges.Should().Contain(s => s.Contains("\"healthResult\"")).And.Contain(s => s.Contains("\"incident\""))
            .And.Contain(s => s.Contains("\"incidentSample\""));
    }
}
