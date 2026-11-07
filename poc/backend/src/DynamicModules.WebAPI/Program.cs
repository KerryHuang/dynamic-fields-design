using System.Text.Json;
using System.Text.Json.Serialization;
using DynamicModules.Application;
using DynamicModules.Application.Common.Acting;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.AttachmentService;
using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Files;
using DynamicModules.Persistence.Seed;
using DynamicModules.WebAPI.Attachments;
using DynamicModules.WebAPI.Demo;
using DynamicModules.WebAPI.Security;
using DynamicModules.WebAPI.Time;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// 相對路徑一律以 content root 解析成絕對路徑，建目錄與開檔用同一個位置。
var dynamicConnection = SqliteConnectionStrings.ResolveDataSource(
    builder.Configuration.GetConnectionString("Dynamic")!, builder.Environment.ContentRootPath);
builder.Configuration["ConnectionStrings:Dynamic"] = dynamicConnection;
// 雲端主機（如 Render）用 PORT 指定埠並要求聽所有介面；本機開發維持 127.0.0.1:5180。
var port = Environment.GetEnvironmentVariable("PORT");
builder.WebHost.UseUrls(port is null ? "http://127.0.0.1:5180" : $"http://0.0.0.0:{port}");
builder.Services.Configure<RouteOptions>(o => o.LowercaseUrls = true);
builder.Services.AddApplication();
builder.Services.AddSingleton(new ReportOptions
{
    MaxRows = builder.Configuration.GetValue("Reports:MaxRows", 50_000),
    MaxLoadRows = builder.Configuration.GetValue("Reports:MaxLoadRows", 200_000),
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddScoped<ICurrentUser, ClaimsCurrentUser>();
builder.Services.AddSingleton<IPasswordService, PasswordService>();
builder.Services.AddSingleton(new AuthSettings(builder.Configuration.GetValue<bool>("Demo:LoginEnabled")));
// 附件：預設放在資料庫旁的 attachments 目錄（記憶體資料庫則放暫存目錄）。Demo 登入開啟時預設不開放上傳，可用 Attachments:UploadEnabled 明確覆寫。
var attachmentSettings = AttachmentSettingsFactory.Create(builder.Configuration, dynamicConnection);
builder.Services.AddSingleton(attachmentSettings);
builder.Services.AddSingleton<IAttachmentStore>(new LocalAttachmentStore(attachmentSettings.Root));
// 鎖定判斷用真實時間；IBusinessTime 會被 ActingScope 覆寫，不能用
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IBusinessTime, TaipeiBusinessTime>();
// 須在 ICurrentUser、IBusinessTime 註冊之後：Demo 資料產生器靠它在範圍內切換身分與時間
builder.Services.AddActingOverrides();
builder.Services.AddSingleton<IDemoDataRunner, DemoDataRunner>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.SlidingExpiration = true;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });
// 站台在反向代理後面（Render）時才信任 X-Forwarded-Proto，否則 Secure cookie 不會生效
var trustForwardedHeaders = builder.Configuration.GetValue<bool>("Auth:TrustForwardedHeaders");
if (trustForwardedHeaders)
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
        o.KnownNetworks.Clear();
        o.KnownProxies.Clear();
    });
// 資料庫是檔案時，DataProtection 金鑰放在同一個目錄，重開站台後登入狀態不失效
var dataSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(dynamicConnection).DataSource;
if (Path.IsPathRooted(dataSource))
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(Path.GetDirectoryName(dataSource)!, "keys")));
builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    // 須在全域 JsonStringEnumConverter 之前：options.Converters 優先於型別 attribute，先到先贏
    o.JsonSerializerOptions.Converters.Add(new LowercaseEnumConverter<FieldType>());
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddExceptionHandler<DynamicModules.WebAPI.Errors.ConcurrencyExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// 內建範本有錯就拒絕啟動（spec §13.1）：在碰資料庫之前先解析 singleton
app.Services.GetRequiredService<TemplateCatalog>();
app.Services.GetRequiredService<DynamicModules.Application.Dynamic.AppService.AppCatalog>();
await InitializeDatabaseAsync(app, dynamicConnection);
// 建置 Demo 映像時用（Dockerfile.demo）：產生完 Demo 資料就結束，失敗回非 0 讓建置失敗
if (args.Contains("--generate-demo-data"))
{
    var runner = app.Services.GetRequiredService<IDemoDataRunner>();
    runner.TryStart();
    while (runner.Current.State == DemoRunState.Running) await Task.Delay(200);
    if (runner.Current.State == DemoRunState.Succeeded) return 0;
    app.Logger.LogError("Demo 資料產生失敗：{State} {Message}", runner.Current.State, runner.Current.Message);
    return 1;
}
// Demo 站台（Render）啟動後在背景產生 Demo 資料；結果只記 log，不擋啟動也不擋 /api/health
if (app.Configuration.GetValue<bool>("Demo:SeedOnStartup"))
    app.Lifetime.ApplicationStarted.Register(() => app.Services.GetRequiredService<IDemoDataRunner>().TryStart(startup: true));

if (app.Services.GetRequiredService<AuthSettings>().DemoLoginEnabled)
    app.Logger.LogWarning("Demo:LoginEnabled 已開啟：任何人都能不輸入密碼以示範帳號或 admin 登入，只能用在公開的 Demo 站");

if (trustForwardedHeaders) app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseAuthentication();
app.UseMiddleware<SessionMiddleware>();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.MapGet("/api/health", () => Results.Json(new { status = "ok" }));

// 部署時前端建置結果放在 wwwroot（Dockerfile），由同一個站台提供；本機開發沒有 wwwroot，前端走 quasar dev。
if (Directory.Exists(Path.Combine(app.Environment.ContentRootPath, "wwwroot")))
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
    // 不存在的 API 照樣回 404，其餘路徑交給前端的 history 路由
    app.Map("/api/{**rest}", () => Results.NotFound());
    app.MapFallbackToFile("index.html");
}

app.Run();
return 0;

static async Task InitializeDatabaseAsync(WebApplication app, string dynamicConnection)
{
    var source = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(dynamicConnection).DataSource;
    if (Path.IsPathRooted(source))
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);

    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
    await db.Database.MigrateAsync();
    await SeedData.EnsureSeededAsync(db);
    await AdminBootstrap.EnsureAsync(db, scope.ServiceProvider.GetRequiredService<IPasswordService>(), app.Configuration,
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(AdminBootstrap)));
}

public partial class Program { }
