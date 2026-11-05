using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.WebAPITests;

/// <summary>Demo 站一鍵登入：Demo:LoginEnabled 開關、帳號清單、不驗密碼、不寫資料庫（Review Focus 5）。</summary>
public class DemoLoginApiTests
{
    private const string Auth = "/api/v3/dynamic/auth";
    private const string Base = "/api/v3/dynamic";
    private const string FailureMessage = "帳號或密碼錯誤，或帳號暫時無法登入";

    private sealed class LoginApp(bool loginEnabled = true, bool seedDemoPeople = true, bool fileDatabase = false)
        : TestApp(seedDemoPeople, realAuth: true, fileDatabase: fileDatabase)
    {
        public bool SeedOnStartup { get; init; }
        public Gate Gate { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(s =>
            {
                s.AddSingleton(Gate);
                s.AddTransient(typeof(IPipelineBehavior<,>), typeof(GateBehavior<,>));
            });
            builder.UseSetting("Demo:LoginEnabled", loginEnabled ? "true" : "false");
            if (SeedOnStartup) builder.UseSetting("Demo:SeedOnStartup", "true");
        }

        protected override void Dispose(bool disposing)
        {
            Gate.Release.TrySetResult(); // 測試中途失敗也不讓背景流程永遠卡住
            base.Dispose(disposing);
        }

        public void Db(Action<DynamicDbContext> act)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            act(db);
        }

        public string UsersSnapshot()
        {
            string snapshot = "";
            Db(db => snapshot = string.Join("\n", db.Set<AppUser>().AsNoTracking().OrderBy(u => u.UserId).AsEnumerable()
                .Select(u => JsonSerializer.Serialize(u))));
            return snapshot;
        }
    }

    /// <summary>Demo 產生器開始執行時停住，直到測試放行（手法同 DemoDataApiTests）。</summary>
    private sealed class Gate
    {
        private int _hit;

        public Func<object, bool> When { get; set; } = _ => false;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PassAsync(object request)
        {
            if (!When(request) || Interlocked.Exchange(ref _hit, 1) == 1) return;
            Entered.SetResult();
            await Release.Task;
        }
    }

    private sealed class GateBehavior<TReq, TRes>(Gate gate) : IPipelineBehavior<TReq, TRes> where TReq : notnull
    {
        public async Task<TRes> Handle(TReq request, RequestHandlerDelegate<TRes> next, CancellationToken ct)
        {
            await gate.PassAsync(request);
            return await next();
        }
    }

    private static Task<HttpResponseMessage> DemoLogin(HttpClient c, string userId) =>
        c.PostAsJsonAsync($"{Auth}/demo-login", new { userId });

    private static async Task<JsonElement> Json(HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Options_Disabled_NoAccounts()
    {
        using var app = new LoginApp(loginEnabled: false);

        var res = await app.CreateClient().GetAsync($"{Auth}/options");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(res);
        body.GetProperty("demoLoginEnabled").GetBoolean().Should().BeFalse();
        body.GetProperty("demoGenerating").GetBoolean().Should().BeFalse();
        body.GetProperty("demoAccounts").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task DemoLogin_Disabled_404()
    {
        using var app = new LoginApp(loginEnabled: false);

        // 即使帳號是合法的示範帳號，先判斷開關
        (await DemoLogin(app.CreateClient(), "sales01")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await DemoLogin(app.CreateClient(), "nobody")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Options_Enabled_ListsActiveDemoAccountsAndAdmin()
    {
        using var app = new LoginApp();
        app.Db(db =>
        {
            db.Set<AppUser>().Add(new AppUser { UserId = "retired01", DisplayName = "已停用", IsDemoAccount = true, IsActive = false });
            db.Set<AppUser>().Add(new AppUser { UserId = "alice", DisplayName = "一般帳號" });
            db.SaveChanges();
        });

        var body = await Json(await app.CreateClient().GetAsync($"{Auth}/options"));

        body.GetProperty("demoLoginEnabled").GetBoolean().Should().BeTrue();
        var accounts = body.GetProperty("demoAccounts").EnumerateArray()
            .Select(a => (a.GetProperty("userId").GetString(), a.GetProperty("displayName").GetString())).ToList();
        accounts[0].Should().Be(("admin", "系統管理員"));
        accounts.Select(a => a.Item1).Skip(1).Should().BeEquivalentTo(
            ["floor01", "floor02", "maint01", "manager01", "qa01", "sales01", "sales02"], o => o.WithStrictOrdering());
        accounts.Should().NotContain(a => a.Item1 == "retired01" || a.Item1 == "alice");
    }

    [Fact]
    public async Task DemoLogin_NonDemoAccount_401()
    {
        using var app = new LoginApp();
        app.Db(db => { db.Set<AppUser>().Add(new AppUser { UserId = "alice", DisplayName = "一般帳號" }); db.SaveChanges(); });

        var res = await DemoLogin(app.CreateClient(), "alice");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Json(res)).GetProperty("message").GetString().Should().Be(FailureMessage);
        (await app.CreateClient().GetAsync($"{Base}/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DemoLogin_UnknownAccount_401()
    {
        using var app = new LoginApp();

        var res = await DemoLogin(app.CreateClient(), "ghost");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Json(res)).GetProperty("message").GetString().Should().Be(FailureMessage);
    }

    [Fact]
    public async Task DemoLogin_InactiveAccount_401()
    {
        using var app = new LoginApp();
        app.Db(db => { db.Set<AppUser>().Single(u => u.UserId == "sales01").IsActive = false; db.SaveChanges(); });

        var res = await DemoLogin(app.CreateClient(), "sales01");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Json(res)).GetProperty("message").GetString().Should().Be(FailureMessage);
    }

    [Fact]
    public async Task DemoLogin_Admin_NoPasswordChangeRequired()
    {
        using var app = new LoginApp();
        var client = app.CreateClient();

        var res = await DemoLogin(client, "admin");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(res)).GetProperty("mustChangePassword").GetBoolean().Should().BeFalse();
        (await client.GetAsync($"{Base}/menu")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DemoLogin_DemoAccount_TrimAndCaseInsensitive()
    {
        using var app = new LoginApp();
        var client = app.CreateClient();

        (await DemoLogin(client, " Sales01 ")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Json(await client.GetAsync($"{Base}/me"))).GetProperty("userId").GetString().Should().Be("sales01");
    }

    [Fact]
    public async Task DemoLogin_LockedAccount_Succeeds()
    {
        using var app = new LoginApp();
        app.Db(db =>
        {
            var u = db.Set<AppUser>().Single(x => x.UserId == "sales01");
            u.LockedUntil = app.Time.GetUtcNow().AddMinutes(10);
            u.FailedLoginCount = 5;
            db.SaveChanges();
        });
        var client = app.CreateClient();

        (await DemoLogin(client, "sales01")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"{Base}/me")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>Review Focus 5：Demo 產生器的寫入交易會鎖表，平常的一鍵登入不能寫資料庫。</summary>
    [Fact]
    public async Task DemoLogin_Admin_DoesNotWrite()
    {
        using var app = new LoginApp();
        app.Db(db => db.Set<AppUser>().Single(u => u.UserId == "admin").MustChangePassword.Should().BeFalse());
        var before = app.UsersSnapshot();

        (await DemoLogin(app.CreateClient(), "admin")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await DemoLogin(app.CreateClient(), "sales01")).StatusCode.Should().Be(HttpStatusCode.OK);

        app.UsersSnapshot().Should().Be(before);
    }

    [Fact]
    public async Task DemoLogin_MustChangeSet_ClearsFlag()
    {
        using var app = new LoginApp();
        app.Db(db => { db.Set<AppUser>().Single(u => u.UserId == "admin").MustChangePassword = true; db.SaveChanges(); });
        var client = app.CreateClient();

        (await DemoLogin(client, "admin")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.GetAsync($"{Base}/menu")).StatusCode.Should().Be(HttpStatusCode.OK);
        app.Db(db => db.Set<AppUser>().AsNoTracking().Single(u => u.UserId == "admin").MustChangePassword.Should().BeFalse());
    }

    [Fact]
    public async Task Options_WhileGenerating_DemoGeneratingTrue()
    {
        using var app = new LoginApp(seedDemoPeople: false, fileDatabase: true) { SeedOnStartup = true };
        app.Gate.When = r => r is DynamicModules.Application.Dynamic.DemoService.Commands.GenerateDemoData.GenerateDemoDataRequest;
        var client = app.CreateClient(); // 啟動 host，背景產生才會開始
        await app.Gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var body = await Json(await client.GetAsync($"{Auth}/options"));

        body.GetProperty("demoGenerating").GetBoolean().Should().BeTrue();
        body.GetProperty("demoAccounts").EnumerateArray().Select(a => a.GetProperty("userId").GetString())
            .Should().Equal("admin");

        app.Gate.Release.SetResult();
    }
}
