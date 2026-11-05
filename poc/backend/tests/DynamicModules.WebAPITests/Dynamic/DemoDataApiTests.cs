using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Common.Acting;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.DemoService.Commands.GenerateDemoData;
using DynamicModules.Application.Dynamic.DemoService.Commands.StartDemoData;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>Demo 資料的背景產生、狀態與產生端點、啟動時自動產生。</summary>
/// <remarks>每個測試自己的 TestApp：Demo 模組代碼固定，共用會互相撞。</remarks>
public sealed class DemoDataApiTests
{
    private const string Status = $"{Base}/demo-data";
    private const string Generate = $"{Base}/demo-data/generate";

    [Fact]
    public async Task Status_FreshDb_NotGenerated()
    {
        using var app = new DemoApp();

        var s = await JsonOf(await app.AsUser().GetAsync(Status));

        s.GetProperty("state").GetString().Should().Be("notGenerated");
        s.GetProperty("conflicts").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Generate_ThenPollUntilGenerated()
    {
        using var app = new DemoApp();
        var admin = app.AsUser();

        var res = await admin.PostAsync(Generate, null);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(res)).GetProperty("state").GetString().Should().Be("running");

        var s = await PollUntilDone(admin);
        s.GetProperty("state").GetString().Should().Be("generated");
        s.GetProperty("total").GetInt32().Should().BePositive();
        s.GetProperty("done").GetInt32().Should().Be(s.GetProperty("total").GetInt32());

        var menu = (await JsonOf(await admin.GetAsync($"{Base}/menu"))).GetRawText();
        menu.Should().Contain("\"repair\"").And.Contain("\"quality\"");
        (await app.AsUser("floor01").GetAsync($"{Base}/me")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Generate_WhileRunning_409()
    {
        using var app = new DemoApp();
        app.Gate.When = r => r is GenerateDemoDataRequest; // 交易開始、還沒寫入就停住
        var admin = app.AsUser();

        (await admin.PostAsync(Generate, null)).StatusCode.Should().Be(HttpStatusCode.OK);
        await app.Gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var second = await admin.PostAsync(Generate, null);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonOf(second)).GetProperty("message").GetString().Should().Be("Demo 資料產生中，請稍候");
        (await JsonOf(await admin.GetAsync(Status))).GetProperty("state").GetString().Should().Be("running");

        app.Gate.Release.SetResult();
        (await PollUntilDone(admin)).GetProperty("state").GetString().Should().Be("generated");
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<Module>().AsNoTracking()
            .Count(m => m.Key == "order").Should().Be(1);
    }

    [Fact]
    public async Task Generate_AfterGenerated_409WithConflicts()
    {
        using var app = new DemoApp();
        var admin = app.AsUser();
        (await admin.PostAsync(Generate, null)).EnsureSuccessStatusCode();
        (await PollUntilDone(admin)).GetProperty("state").GetString().Should().Be("generated");

        var res = await admin.PostAsync(Generate, null);

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await JsonOf(res);
        body.GetProperty("message").GetString().Should().Be("已有相同的資料，未產生 Demo 資料");
        body.GetProperty("items").EnumerateArray().Select(i => i.GetString()).Should().Contain("模組代碼 order 已存在");
    }

    [Fact]
    public async Task Generate_NeedsDesignerAddAndEdit()
    {
        using var app = new DemoApp();
        app.AddUsers("viewer01", "adder01"); // 不用 sales01 等示範帳號名，否則 Preflight 會回報衝突
        (await app.AsUser("viewer01").PostAsync(Generate, null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            db.Set<UserProgramPrivilege>().Add(new UserProgramPrivilege
            {
                UserId = "adder01", ProgramCode = "DYN00000", CanRead = true, CanAdd = true
            });
            db.SaveChanges();
        }
        var addOnly = app.AsUser("adder01");
        (await addOnly.GetAsync(Status)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await addOnly.PostAsync(Generate, null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await JsonOf(await addOnly.GetAsync(Status))).GetProperty("state").GetString().Should().Be("notGenerated");
    }

    [Fact]
    public async Task Status_NonDesigner_403()
    {
        using var app = new DemoApp();
        app.AddUsers("sales01");

        (await app.AsUser("sales01").GetAsync(Status)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SeedOnStartup_True_GeneratesInBackground()
    {
        using var app = new DemoApp { SeedOnStartup = true };
        app.Gate.When = r => r is GenerateDemoDataRequest;
        var admin = app.AsUser();

        await app.Gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        (await app.CreateClient().GetAsync("/api/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(await admin.GetAsync(Status))).GetProperty("state").GetString().Should().Be("running");

        app.Gate.Release.SetResult();
        (await PollUntilDone(admin)).GetProperty("state").GetString().Should().Be("generated");
    }

    [Fact]
    public async Task SeedOnStartup_Default_DoesNothing()
    {
        using var app = new DemoApp();
        var admin = app.AsUser();
        (await app.CreateClient().GetAsync("/api/health")).EnsureSuccessStatusCode();

        await Task.Delay(TimeSpan.FromSeconds(2));

        (await JsonOf(await admin.GetAsync(Status))).GetProperty("state").GetString().Should().Be("notGenerated");
    }

    /// <summary>Review Focus 4：背景產生正以模擬身分與時間執行時，其他請求看到的是自己的身分與真實時間。</summary>
    [Fact]
    public async Task WhileGenerating_OtherRequest_SeesOwnIdentityAndRealTime()
    {
        using var app = new DemoApp(fileDatabase: true); // in-memory 共用快取會在產生中鎖住 Dynamic_AppUser，其他請求讀不到
        app.AddUsers("guest01"); // 產生器自己會建 sales01，產生中還看不到，探針用先建好的帳號
        app.Gate.When = _ => ActingScope.Now is not null; // 時間線第一步：身分與時間都被覆寫
        var admin = app.AsUser();

        (await admin.PostAsync(Generate, null)).StatusCode.Should().Be(HttpStatusCode.OK);
        await app.Gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
        var inside = app.Gate.Seen;
        inside.UserId.Should().NotBeNullOrEmpty();
        inside.Now!.Value.Should().BeBefore(DateTime.Today);
        inside.HasHttpContext.Should().BeFalse("背景流程不該帶著觸發它的 HTTP 請求");
        inside.RequestMarker.Should().BeNull("觸發端的 AsyncLocal 不該流進背景流程（ExecutionContext.SuppressFlow）");

        var probe = await JsonOf(await app.AsUser("guest01").GetAsync(DemoApp.ProbePath));
        probe.GetProperty("userId").GetString().Should().Be("guest01");
        probe.GetProperty("now").GetDateTime().Should().BeCloseTo(DateTime.Now, TimeSpan.FromMinutes(1));

        app.Gate.Release.SetResult();
        (await PollUntilDone(admin)).GetProperty("state").GetString().Should().Be("generated");
        var me = await JsonOf(await app.AsUser("sales01").GetAsync($"{Base}/me"));
        me.GetProperty("userId").GetString().Should().Be("sales01");
    }

    /// <summary>每 200 ms 輪詢，直到不是 running（最多 120 秒）。</summary>
    private static async Task<JsonElement> PollUntilDone(HttpClient admin)
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (true)
        {
            var s = await JsonOf(await admin.GetAsync(Status));
            if (s.GetProperty("state").GetString() != "running") return s;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Demo 資料 120 秒內沒有產生完");
            await Task.Delay(200);
        }
    }

    /// <summary>可設定 Demo:SeedOnStartup、可在 pipeline 裡卡住背景產生，並提供一個回報目前身分與時間的探針端點。</summary>
    private sealed class DemoApp : TestApp
    {
        public const string ProbePath = "/test/acting";

        public DemoApp(bool fileDatabase = false) : base(seedDemoPeople: false, fileDatabase: fileDatabase) { }

        /// <summary>示範人員由產生器建立，需要非 admin 身分的測試自己補帳號。</summary>
        public void AddUsers(params string[] ids)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            foreach (var id in ids) db.Set<AppUser>().Add(new AppUser { UserId = id, DisplayName = id });
            db.SaveChanges();
        }

        public bool SeedOnStartup { get; init; }
        public DemoGate Gate { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            if (SeedOnStartup) builder.UseSetting("Demo:SeedOnStartup", "true");
            builder.ConfigureServices(s =>
            {
                s.AddSingleton(Gate);
                s.AddTransient(typeof(IPipelineBehavior<,>), typeof(GateBehavior<,>));
                s.AddTransient<IStartupFilter, ProbeStartupFilter>();
            });
        }

        protected override void Dispose(bool disposing)
        {
            Gate.Release.TrySetResult(); // 測試中途失敗也不讓背景流程永遠卡住
            base.Dispose(disposing);
        }
    }

    private sealed class DemoGate
    {
        private int _hit;

        /// <summary>在觸發產生的 HTTP 請求流程裡設定；背景流程看得到就表示 ExecutionContext 流過去了。</summary>
        public static readonly AsyncLocal<string?> RequestMarker = new();

        public Func<object, bool> When { get; set; } = _ => false;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public (string? UserId, DateTime? Now, bool HasHttpContext, string? RequestMarker) Seen { get; private set; }

        public async Task PassAsync(object request, IHttpContextAccessor http)
        {
            if (!When(request) || Interlocked.Exchange(ref _hit, 1) == 1) return;
            Seen = (ActingScope.UserId, ActingScope.Now, http.HttpContext is not null, RequestMarker.Value);
            Entered.SetResult();
            await Release.Task;
        }
    }

    private sealed class GateBehavior<TReq, TRes>(DemoGate gate, IHttpContextAccessor http) : IPipelineBehavior<TReq, TRes>
        where TReq : notnull
    {
        public async Task<TRes> Handle(TReq request, RequestHandlerDelegate<TRes> next, CancellationToken ct)
        {
            if (request is StartDemoDataRequest) DemoGate.RequestMarker.Value = "start-request";
            await gate.PassAsync(request, http);
            return await next();
        }
    }

    /// <summary>不碰資料庫的探針：產生中的交易鎖著表，走一般 API 會被擋住。</summary>
    private sealed class ProbeStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseAuthentication(); // 探針在 Program 的 pipeline 之前，自己先解析身分
            app.Map(DemoApp.ProbePath, a => a.Run(async ctx =>
            {
                var user = ctx.RequestServices.GetRequiredService<ICurrentUser>();
                var time = ctx.RequestServices.GetRequiredService<IBusinessTime>();
                await ctx.Response.WriteAsJsonAsync(new { userId = user.UserId, now = time.Now });
            }));
            next(app);
        };
    }
}
