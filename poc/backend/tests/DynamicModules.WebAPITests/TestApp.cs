using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Data;
using DynamicModules.Application.Common.Acting;
using DynamicModules.Application.Common.Time;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using DynamicModules.Persistence.Seed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace DynamicModules.WebAPITests;

/// <summary>共用 SQLite in-memory 的測試主機；靠一條保持開啟的連線維持資料庫存活。</summary>
public class TestApp : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString = $"Data Source=file:test{Guid.NewGuid():N}?mode=memory&cache=shared";

    private readonly bool _seedDemoPeople;

    /// <param name="seedDemoPeople">host 建好後補上示範人員（sales01 等）；Demo 產生器的測試要設 false，否則會衝突。</param>
    /// <param name="realAuth">Task 2 才有作用。</param>
    public TestApp() : this(true) { }

    // internal：xUnit 的 class fixture 只認 public 建構函式，不會拿它去解析參數
    internal TestApp(bool seedDemoPeople, bool realAuth = false)
    {
        _seedDemoPeople = seedDemoPeople;
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    /// <summary>可設定的假時鐘；預設是真實時間，測試需要固定日期時再指定 Now。</summary>
    public FakeBusinessTime Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Dynamic", _connectionString);
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<IBusinessTime>();
            // RemoveAll 也拿掉了 Program 註冊的 Acting 裝飾器，這裡補回去
            s.AddSingleton<IBusinessTime>(new ActingBusinessTime(Clock));
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        if (_seedDemoPeople)
        {
            using var scope = host.Services.CreateScope();
            DemoPeople.EnsureAsync(scope.ServiceProvider.GetRequiredService<DynamicDbContext>()).GetAwaiter().GetResult();
        }
        return host;
    }

    /// <summary>預設以 admin 身分；傳 null 則完全不帶 X-User。</summary>
    public HttpClient AsUser(string? userId = "admin")
    {
        var client = CreateClient();
        if (userId is not null) client.DefaultRequestHeaders.Add("X-User", userId);
        return client;
    }

    /// <summary>
    /// 測試內新建的模組沒有任何授權（只有升級時的既有模組會補）；給業務、業務主管、現場三個群組全部動作，資料範圍不限。
    /// </summary>
    public void GrantBusinessGroups(string moduleKey)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        var code = db.Set<Module>().Single(m => m.Key == moduleKey).ProgramCode;
        foreach (var group in db.Set<PermissionGroup>().Where(g => g.Key != "mis").ToList())
        {
            db.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
            {
                GroupId = group.Id, ProgramCode = code, CanRead = true, CanAdd = true, CanEdit = true,
                CanDelete = true, CanPrint = true, CanAmt = true
            });
        }
        db.SaveChanges();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _keepAlive.Dispose();
    }
}

public sealed class FakeBusinessTime : IBusinessTime
{
    public DateTime? Fixed { get; set; }
    public DateTime Now => Fixed ?? DateTime.Now;
    public DateOnly Today => DateOnly.FromDateTime(Now);
}
