using System.Text.Json;
using System.Text.Json.Serialization;
using DynamicModules.Application;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Seed;
using Microsoft.EntityFrameworkCore;
using DynamicModules.WebAPI.Security;
using DynamicModules.WebAPI.Time;

var builder = WebApplication.CreateBuilder(args);

// 相對路徑一律以 content root 解析成絕對路徑，建目錄與開檔用同一個位置。
var dynamicConnection = SqliteConnectionStrings.ResolveDataSource(
    builder.Configuration.GetConnectionString("Dynamic")!, builder.Environment.ContentRootPath);
builder.Configuration["ConnectionStrings:Dynamic"] = dynamicConnection;
builder.WebHost.UseUrls("http://127.0.0.1:5180");
builder.Services.Configure<RouteOptions>(o => o.LowercaseUrls = true);
builder.Services.AddApplication();
builder.Services.AddHttpContextAccessor();
builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddScoped<ICurrentUser, HeaderCurrentUser>();
builder.Services.AddSingleton<IBusinessTime, TaipeiBusinessTime>();
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
await InitializeDatabaseAsync(app, dynamicConnection);

app.UseExceptionHandler();
app.UseMiddleware<IdentityMiddleware>();
app.UseSwagger();
app.UseSwaggerUI();

app.MapControllers();
app.MapGet("/api/health", () => Results.Json(new { status = "ok" }));

app.Run();

static async Task InitializeDatabaseAsync(WebApplication app, string dynamicConnection)
{
    var source = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(dynamicConnection).DataSource;
    if (Path.IsPathRooted(source))
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);

    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
    await db.Database.MigrateAsync();
    await SeedData.EnsureSeededAsync(db);
}

public partial class Program { }
