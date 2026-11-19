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
using DynamicModules.TestSupport;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>Demo 產生類測試（每個都完整跑一次約 900 步、近 9 萬條 SQL）獨佔執行：不與其他測試類別平行，避免全套高負載時輪詢逾時。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DemoGenerationCollection
{
    public const string Name = "DemoGeneration";
}

/// <summary>Demo 資料的背景產生、狀態與產生端點、啟動時自動產生。</summary>
/// <remarks>
/// Demo 模組代碼固定，不同 TestApp 不能共用資料庫，所以分兩類：
/// 需要完整產生結果、只讀它的測試共用類別 fixture 的一份產生（<see cref="DemoGeneratedFixture"/>，把「產生中」的觀察與放行階段都在那一次產生裡做完）；
/// 不需要產生的測試、與必須從啟動設定觸發產生的 <c>SeedOnStartup_True</c> 各自建 TestApp。
/// </remarks>
[Collection(DemoGenerationCollection.Name)]
public sealed class DemoDataApiTests(DemoDataApiTests.DemoGeneratedFixture gen) : IClassFixture<DemoDataApiTests.DemoGeneratedFixture>
{
    private const string Status = $"{Base}/demo-data";
    private const string Generate = $"{Base}/demo-data/generate";

    /// <summary>
    /// 整個類別只產生一次的 Demo：POST 觸發，並依序停在兩個關卡錄下「產生中」的觀察（重複觸發 409、狀態 running、背景身分與時間），
    /// 放行後等到 generated。觀察值只記錄、不在這裡斷言，由各測試各自斷言（初始化期間只有逾時等基礎設施錯誤會拋出）。
    /// </summary>
    public sealed class DemoGeneratedFixture : IAsyncLifetime
    {
        internal DemoApp App { get; } = new(fileDatabase: true);
        internal HttpClient Admin { get; private set; } = null!;

        public HttpStatusCode FirstPostStatus { get; private set; }
        public JsonElement FirstPostBody { get; private set; }
        public HttpStatusCode SecondPostStatus { get; private set; }
        public JsonElement SecondPostBody { get; private set; }
        public JsonElement StatusWhileRunning { get; private set; }
        public (string? UserId, DateTime? Now, bool HasHttpContext, string? RequestMarker) Inside { get; private set; }
        public JsonElement Probe { get; private set; }
        public DateTime ProbeAt { get; private set; }
        public JsonElement Final { get; private set; }

        public async Task InitializeAsync()
        {
            App.AddUsers("guest01"); // 產生器自己會建 sales01，產生中還看不到，探針用先建好的帳號
            App.Gate.When = r => r is GenerateDemoDataRequest; // 關卡一：交易開始、還沒寫入就停住
            App.Gate2.When = _ => ActingScope.Now is not null; // 關卡二：時間線第一步，身分與時間都被覆寫
            Admin = App.AsUser();

            var first = await Admin.PostAsync(Generate, null);
            FirstPostStatus = first.StatusCode;
            FirstPostBody = await JsonOf(first);
            await WaitGateAsync("關卡一（GenerateDemoDataRequest）", App.Gate, TimeSpan.FromSeconds(30));

            var second = await Admin.PostAsync(Generate, null);
            SecondPostStatus = second.StatusCode;
            SecondPostBody = await JsonOf(second);
            StatusWhileRunning = await JsonOf(await Admin.GetAsync(Status));

            App.Gate.Release.SetResult();
            await WaitGateAsync("關卡二（ActingScope.Now 已覆寫的第一步）", App.Gate2, TimeSpan.FromSeconds(60));
            Inside = App.Gate2.Seen;
            Probe = await JsonOf(await App.AsUser("guest01").GetAsync(DemoApp.ProbePath));
            ProbeAt = DateTime.Now;

            App.Gate2.Release.SetResult();
            Final = await PollUntilDone(Admin);
        }

        /// <summary>等關卡進入；背景產生已不是 running（提早失敗或完成）就立刻報錯，訊息帶關卡名、首次 POST 與 runner 狀態快照。</summary>
        private async Task WaitGateAsync(string name, DemoGate gate, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                if (await Task.WhenAny(gate.Entered.Task, Task.Delay(200)) == gate.Entered.Task) return;
                var snapshot = await JsonOf(await Admin.GetAsync(Status));
                var state = snapshot.TryGetProperty("state", out var st) ? st.GetString() : null;
                if (state != "running" || DateTime.UtcNow > deadline)
                    throw new InvalidOperationException(
                        $"等不到{name}（{(state != "running" ? "背景產生已不是 running" : $"逾時 {timeout.TotalSeconds:0} 秒")}）。" +
                        $"首次 POST：{FirstPostStatus} {FirstPostBody}；runner 狀態：{snapshot}");
            }
        }

        public Task DisposeAsync()
        {
            App.Dispose();
            return Task.CompletedTask;
        }
    }

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
        gen.FirstPostStatus.Should().Be(HttpStatusCode.OK);
        gen.FirstPostBody.GetProperty("state").GetString().Should().Be("running");

        var s = gen.Final;
        s.GetProperty("state").GetString().Should().Be("generated", s.ToString());
        s.GetProperty("total").GetInt32().Should().BePositive();
        s.GetProperty("done").GetInt32().Should().Be(s.GetProperty("total").GetInt32());

        var menu = (await JsonOf(await gen.Admin.GetAsync($"{Base}/menu"))).GetRawText();
        menu.Should().Contain("\"mes-repair\"").And.Contain("\"mes-quality\"");
        (await gen.App.AsUser("floor01").GetAsync($"{Base}/me")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void Generate_WhileRunning_409()
    {
        gen.FirstPostStatus.Should().Be(HttpStatusCode.OK);
        gen.SecondPostStatus.Should().Be(HttpStatusCode.Conflict);
        gen.SecondPostBody.GetProperty("message").GetString().Should().Be("Demo 資料產生中，請稍候");
        gen.StatusWhileRunning.GetProperty("state").GetString().Should().Be("running");

        gen.Final.GetProperty("state").GetString().Should().Be("generated");
        using var scope = gen.App.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<Module>().AsNoTracking()
            .Count(m => m.Key == "mes-order").Should().Be(1);
    }

    [Fact]
    public async Task Generate_AfterGenerated_409WithConflicts()
    {
        gen.FirstPostStatus.Should().Be(HttpStatusCode.OK);
        gen.Final.GetProperty("state").GetString().Should().Be("generated");

        var res = await gen.Admin.PostAsync(Generate, null);

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await JsonOf(res);
        body.GetProperty("message").GetString().Should().Be("已有相同的資料，未產生 Demo 資料");
        body.GetProperty("items").EnumerateArray().Select(i => i.GetString()).Should().Contain("模組代碼 mes-order 已存在");
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
            db.SaveForTests();
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
        using var app = new DemoApp(fileDatabase: true) { SeedOnStartup = true };
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
        gen.FirstPostStatus.Should().Be(HttpStatusCode.OK);
        var inside = gen.Inside; // 產生中（關卡二）錄下的背景流程所見
        inside.UserId.Should().NotBeNullOrEmpty();
        inside.Now!.Value.Should().BeBefore(DateTime.Today);
        inside.HasHttpContext.Should().BeFalse("背景流程不該帶著觸發它的 HTTP 請求");
        inside.RequestMarker.Should().BeNull("觸發端的 AsyncLocal 不該流進背景流程（ExecutionContext.SuppressFlow）");

        gen.Probe.GetProperty("userId").GetString().Should().Be("guest01");
        gen.Probe.GetProperty("now").GetDateTime().Should().BeCloseTo(gen.ProbeAt, TimeSpan.FromMinutes(1));

        gen.Final.GetProperty("state").GetString().Should().Be("generated");
        var me = await JsonOf(await gen.App.AsUser("sales01").GetAsync($"{Base}/me"));
        me.GetProperty("userId").GetString().Should().Be("sales01");
    }

    /// <summary>
    /// 每 200 ms 輪詢，直到不是 running。以「進度停滯」判定卡死（done 連續 StallLimit 沒前進才逾時），
    /// 不以總耗時判定：機器負載高（多個專案／worktree 同時跑測試）時產生變慢但仍在前進，不算失敗。
    /// 另設總時間上限防止無限等。
    /// </summary>
    private static async Task<JsonElement> PollUntilDone(HttpClient admin)
    {
        var stallLimit = TimeSpan.FromSeconds(120);
        var hardDeadline = DateTime.UtcNow.AddMinutes(30);
        var lastDone = -1;
        var lastProgressAt = DateTime.UtcNow;
        while (true)
        {
            var s = await JsonOf(await admin.GetAsync(Status));
            if (s.GetProperty("state").GetString() != "running") return s;
            var done = s.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0;
            if (done != lastDone) { lastDone = done; lastProgressAt = DateTime.UtcNow; }
            if (DateTime.UtcNow - lastProgressAt > stallLimit) throw new TimeoutException($"Demo 資料產生停滯：done={done} 已 {stallLimit.TotalSeconds:0} 秒沒前進");
            if (DateTime.UtcNow > hardDeadline) throw new TimeoutException("Demo 資料 30 分鐘內沒有產生完");
            await Task.Delay(200);
        }
    }

    /// <summary>可設定 Demo:SeedOnStartup、可在 pipeline 裡卡住背景產生，並提供一個回報目前身分與時間的探針端點。</summary>
    internal sealed class DemoApp : TestApp
    {
        public const string ProbePath = "/test/acting";

        public DemoApp(bool fileDatabase = false) : base(seedDemoPeople: false, fileDatabase: fileDatabase) { }

        /// <summary>示範人員由產生器建立，需要非 admin 身分的測試自己補帳號。</summary>
        public void AddUsers(params string[] ids)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            foreach (var id in ids) db.Set<AppUser>().Add(new AppUser { UserId = id, DisplayName = id });
            db.SaveForTests();
        }

        public bool SeedOnStartup { get; init; }
        public DemoGate Gate { get; } = new();
        public DemoGate Gate2 { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            if (SeedOnStartup) builder.UseSetting("Demo:SeedOnStartup", "true");
            builder.ConfigureServices(s =>
            {
                s.AddSingleton(Gate);
                s.AddSingleton(Gate2);
                s.AddTransient(typeof(IPipelineBehavior<,>), typeof(GateBehavior<,>));
                s.AddTransient<IStartupFilter, ProbeStartupFilter>();
            });
        }

        protected override void Dispose(bool disposing)
        {
            Gate.Release.TrySetResult();
            Gate2.Release.TrySetResult(); // 測試中途失敗也不讓背景流程永遠卡住
            base.Dispose(disposing);
        }
    }

    internal sealed class DemoGate
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

    private sealed class GateBehavior<TReq, TRes>(IEnumerable<DemoGate> gates, IHttpContextAccessor http) : IPipelineBehavior<TReq, TRes>
        where TReq : notnull
    {
        public async Task<TRes> Handle(TReq request, RequestHandlerDelegate<TRes> next, CancellationToken ct)
        {
            if (request is StartDemoDataRequest) DemoGate.RequestMarker.Value = "start-request";
            foreach (var gate in gates) await gate.PassAsync(request, http);
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
