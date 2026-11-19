using System.Globalization;
using Microsoft.AspNetCore.Builder;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using DynamicModules.Application.Common.Acting;
using DynamicModules.Application.Common.Time;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using DynamicModules.Persistence.Postgres;
using DynamicModules.Persistence.Seed;
using DynamicModules.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace DynamicModules.WebAPITests;

/// <summary>
/// 測試主機。預設共用 SQLite in-memory，靠一條保持開啟的連線維持資料庫存活；<c>FORMWRIGHT_TEST_DB=postgres</c> 時改用
/// PostgreSQL 測試伺服器上從樣板複製的一個資料庫（PostgreSQL provider 設計 §6.2、§6.3），<c>Database:Provider=Postgres</c>，
/// 走 Program 的正式註冊（AddPostgresDatabase），Dispose 時刪掉資料庫。
/// </summary>
public class TestApp : WebApplicationFactory<Program>
{
    private readonly SqliteConnection? _keepAlive;
    /// <summary>PostgreSQL 模式下由這個主機建立、Dispose 時要刪掉的資料庫；接別人的庫（sharedConnectionString）時是 false。</summary>
    private readonly bool _ownsPostgresDatabase;
    private readonly string _connectionString;
    private readonly string? _databaseFile;

    /// <summary>fileDatabase 模式的資料庫檔路徑；否則為 null。</summary>
    internal string? DatabaseFile => _databaseFile;

    internal string ConnectionString => _connectionString;

    private readonly bool _seedDemoPeople;
    private readonly bool _realAuth;

    public TestApp() : this(true) { }

    // internal：xUnit 的 class fixture 只認 public 建構函式，不會拿它去解析參數
    /// <param name="seedDemoPeople">host 建好後補上示範人員（sales01 等）；Demo 產生器的測試要設 false，否則會衝突。</param>
    /// <param name="realAuth">true 走真實 cookie 登入；false 以 X-User 標頭驗證（TestAuthHandler），並解除 admin 的強制改密碼。</param>
    /// <param name="fileDatabase">改用暫存檔 SQLite（Dispose 時刪除）。共用快取的 in-memory 資料庫在寫入交易進行中會讓其他連線讀同一張表時噴 table is locked，檔案模式才是正式站的行為。PostgreSQL 模式本來就是真的多連線，忽略這個參數。</param>
    /// <param name="sharedConnectionString">接上另一個 TestApp 的資料庫（對方必須還活著），用來測「重新啟動」時的行為。</param>
    internal TestApp(bool seedDemoPeople = true, bool realAuth = false, bool fileDatabase = false, string? sharedConnectionString = null)
    {
        _seedDemoPeople = seedDemoPeople;
        _realAuth = realAuth;
        if (TestDatabase.IsPostgres)
        {
            _ownsPostgresDatabase = sharedConnectionString is null;
            _connectionString = sharedConnectionString ?? PostgresTestServer.CreateDatabase();
            return;
        }
        if (sharedConnectionString is not null) _connectionString = sharedConnectionString;
        else if (fileDatabase)
        {
            _databaseFile = Path.Combine(Path.GetTempPath(), $"dm-test-{Guid.NewGuid():N}.db");
            _connectionString = $"Data Source={_databaseFile}";
        }
        else _connectionString = $"Data Source=file:test{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
        // 從已 migrate 的範本整份複製（host 啟動時的 MigrateAsync 只剩檢查歷程）；接別人的庫時對方已經有資料，不覆蓋
        if (sharedConnectionString is null) MigratedDatabaseTemplate.RestoreInto(_keepAlive);
        if (fileDatabase) UseWal(_keepAlive);
    }

    /// <summary>
    /// 檔案模式的資料庫要先切成 WAL。正式站的新資料庫由 EF Migrate 建立，Migrate 在「檔案不存在」時才會設 WAL；
    /// 這裡保持開啟的連線已經先把空檔建出來，Migrate 就不設，資料庫停在預設的 rollback journal，
    /// 長交易（Demo 產生）快取溢出時會握著獨佔鎖直到提交，其他連線的讀取等不到鎖而噴 database is locked。
    /// </summary>
    private static void UseWal(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode = wal";
        cmd.ExecuteNonQuery();
    }

    /// <summary>附件的本機儲存根目錄（每個 TestApp 一個，Dispose 時刪除），測試可直接檢查磁碟上有什麼。</summary>
    internal string AttachmentRoot { get; } = Path.Combine(Path.GetTempPath(), $"dm-att-{Guid.NewGuid():N}");

    /// <summary>額外的設定值（如 Attachments:MaxBytes）；須在 host 啟動前指定。</summary>
    internal Dictionary<string, string?> Settings { get; } = new();

    /// <summary>可設定的假時鐘；預設是真實時間，測試需要固定日期時再指定 Now。</summary>
    public FakeBusinessTime Clock { get; } = new();

    /// <summary>鎖定判斷用的假時鐘（TimeProvider）；與 Clock（業務時間）無關。</summary>
    internal FakeTimeProvider Time { get; } = new();

    /// <summary>啟動時的 Auth:InitialAdminPassword；設成 null 代表沒設環境變數。須在 host 啟動前指定。</summary>
    internal string? InitialAdminPassword { get; set; } = "initial-pass-1";

    internal TestLogSink Logs { get; } = new();

    private long _bodyBytesRead;

    /// <summary>自上次 <see cref="ResetBodyCount"/> 起，伺服器從請求主體讀走的位元組數（用來證明被拒絕的請求沒有被整個收進來）。</summary>
    internal long BodyBytesRead => Interlocked.Read(ref _bodyBytesRead);

    internal void ResetBodyCount() => Interlocked.Exchange(ref _bodyBytesRead, 0);

    private void AddBodyCounter(long n) => Interlocked.Add(ref _bodyBytesRead, n);

    private sealed class CountingBodyFilter(TestApp app) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
        {
            builder.Use(async (context, nextMiddleware) =>
            {
                context.Request.Body = new CountingStream(context.Request.Body, app.AddBodyCounter);
                await nextMiddleware();
            });
            next(builder);
        };
    }

    private sealed class CountingStream(Stream inner, Action<long> onRead) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { var n = inner.Read(buffer, offset, count); onRead(n); return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await inner.ReadAsync(buffer, ct);
            onRead(n);
            return n;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>在 <c>ConfigureServices</c> 末尾執行的服務替換（要在第一次用到 host 之前設定；例如把簽核擋門換成開啟版）。預設 null，既有測試不受影響。</summary>
    internal Action<IServiceCollection>? ExtraServices { get; set; }

    /// <summary>
    /// PostgreSQL 的「無鎖模式」（PostgreSQL provider 設計 §5.6、§7，主控判斷 T4）：把寫入設定換成 <see cref="PostgresWriteSettings.Unserialized"/>，
    /// 閘門與全庫鎖都不做事，同一行程的兩個排程器實例才是真的同時。要在第一次用到 host 之前設定；SQLite 模式沒有作用。
    /// </summary>
    internal bool WithoutWriteSerialization { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Dynamic", _connectionString);
        if (TestDatabase.IsPostgres)
        {
            builder.UseSetting("Database:Provider", "Postgres");
            // 全套在 PostgreSQL 上平行跑時，測試容器是所有測試主機共用的瓶頸，開應用程式這類長交易會讓同庫排隊的寫入等超過正式預設的 30 秒；
            // 測試只驗正確性，等鎖上限放寬（要測忙碌的案例在下面的 Settings 自己設回小值）
            builder.UseSetting("Database:LockTimeoutSeconds", PostgresLockTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        }
        builder.UseSetting("Attachments:Root", AttachmentRoot);
        // 背景排程器預設不跑：否則每個測試主機一啟動就補通知，改到既有測試的通知筆數；排程測試自己建 JobRunner 呼叫 TickAsync
        builder.UseSetting("Ops:Scheduler:Enabled", "false");
        // 看門狗與事件收件匣的背景迴圈同理不跑；維運測試直接呼叫 OpsWatchdog.TickAsync、IncidentInboxDrain.FlushAsync
        builder.UseSetting("Ops:Watchdog:Enabled", "false");
        foreach (var (key, value) in Settings) builder.UseSetting(key, value);
        // 空字串覆蓋 appsettings.Development.json 的開發用密碼，代表「沒設」
        builder.UseSetting("Auth:InitialAdminPassword", InitialAdminPassword ?? "");
        builder.ConfigureLogging(l =>
        {
            l.AddProvider(Logs);
            // 2xx 的請求結束行是 Debug；provider 專屬規則優先於 appsettings 的 Default: Information
            l.AddFilter<TestLogSink>("DynamicModules.WebAPI.Ops", LogLevel.Debug);
            // Development 設定會把每條 EF SQL 以 Information 寫進 stdout／trx（一個類別數百 MB）並被 TestLogSink 收進記憶體；沒有測試斷言 SQL log，測試主機只留 Warning 以上
            l.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
            l.AddFilter("Microsoft.EntityFrameworkCore.Migrations", LogLevel.Warning);
        });
        builder.ConfigureServices(s =>
        {
            s.AddSingleton<IStartupFilter>(new CountingBodyFilter(this));
            s.AddTestEntities();
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Time);
            if (!_realAuth)
            {
                // 啟動引導的 admin 一開始必須改密碼；X-User 模式的測試不走登入，在啟動完成前（背景產生器開始前）解除
                s.AddHostedService<ClearMustChangePassword>();
                s.AddAuthentication(o => o.DefaultScheme = TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            }
            s.RemoveAll<IBusinessTime>();
            // RemoveAll 也拿掉了 Program 註冊的 Acting 裝飾器，這裡補回去
            s.AddSingleton<IBusinessTime>(new ActingBusinessTime(Clock));
            if (TestDatabase.IsPostgres && WithoutWriteSerialization)
            {
                s.RemoveAll<PostgresWriteSettings>();
                s.AddSingleton(PostgresWriteSettings.Unserialized());
            }
            AddWriteGuard(s);
            ExtraServices?.Invoke(s);
        });
    }

    /// <summary>
    /// 在 Program 註冊的 DbContext 選項上疊加測試期寫入守衛（PostgreSQL provider 設計 §5.4）。
    /// EF Core 8 沒有事後追加選項的 API，只能把既有的選項註冊包一層，原本的設定（連線字串、mmap 攔截器）照舊。
    /// </summary>
    private static void AddWriteGuard(IServiceCollection services)
    {
        var original = services.Single(d => d.ServiceType == typeof(DbContextOptions<DynamicDbContext>));
        services.Remove(original);
        services.Add(new ServiceDescriptor(typeof(DbContextOptions<DynamicDbContext>), sp =>
            new DbContextOptionsBuilder<DynamicDbContext>((DbContextOptions<DynamicDbContext>)original.ImplementationFactory!(sp))
                .AddInterceptors(WriteGuardInterceptor.Instance)
                .Options, original.Lifetime));
    }

    /// <remarks>
    /// PostgreSQL 模式整段在執行緒池上跑：主機啟動（hosted service）與下面的種子是同步等非同步，Npgsql 是真的非同步 I/O，
    /// 續傳若回到 xUnit 的同步內容（執行緒數有限、平行的測試類別都在同步等），會整個卡死。SQLite 的非同步實際上同步完成，維持原樣。
    /// </remarks>
    protected override IHost CreateHost(IHostBuilder builder) =>
        TestDatabase.IsPostgres ? Task.Run(() => CreateHostCore(builder)).GetAwaiter().GetResult() : CreateHostCore(builder);

    private IHost CreateHostCore(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        if (_seedDemoPeople)
        {
            using var scope = host.Services.CreateScope();
            DemoPeople.EnsureAsync(scope.ServiceProvider.GetRequiredService<DynamicDbContext>()).GetAwaiter().GetResult();
        }
        return host;
    }

    /// <summary>PostgreSQL 模式的等鎖上限與用戶端逾時（秒）：全套平行跑時單一請求可能排隊很久，不讓 HttpClient 預設的 100 秒先斷掉。</summary>
    private const int PostgresLockTimeoutSeconds = 300;

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        if (TestDatabase.IsPostgres) client.Timeout = TimeSpan.FromSeconds(PostgresLockTimeoutSeconds + 60);
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
        if (!disposing) return;
        _keepAlive?.Dispose();
        if (_ownsPostgresDatabase) PostgresTestServer.DropDatabase(_connectionString);
        if (Directory.Exists(AttachmentRoot)) Directory.Delete(AttachmentRoot, recursive: true);
        if (_databaseFile is null) return;
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_databaseFile)!, Path.GetFileName(_databaseFile) + "*"))
            File.Delete(f);
    }
}

/// <summary>業務時間的假時鐘；Now 一律 Kind=Unspecified，與正式的 TaipeiBusinessTime 相同（PostgreSQL 的 timestamp 欄位不收 Kind=Utc）。</summary>
public sealed class FakeBusinessTime : IBusinessTime
{
    public DateTime? Fixed { get; set; }
    public DateTime Now => DateTime.SpecifyKind(Fixed ?? DateTime.Now, DateTimeKind.Unspecified);
    public DateOnly Today => DateOnly.FromDateTime(Now);
}

internal sealed class ClearMustChangePassword(IServiceScopeFactory scopes) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AppUser>()
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.MustChangePassword, false), ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>可手動快轉的 TimeProvider，起點是真實時間。</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>一則被收集的 log：Values 是 state 欄位加上所有 scope 的 key/value 攤平。</summary>
public sealed record LogRecord(LogLevel Level, string Category, EventId EventId, string Message, IReadOnlyDictionary<string, object?> Values);

/// <summary>收集 log，供斷言啟動時印出的訊息與結構化欄位。</summary>
public sealed class TestLogSink : ILoggerProvider, ISupportExternalScope
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<LogRecord> _records = new();
    private IExternalScopeProvider? _scopes;

    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries.ToArray();

    public IReadOnlyList<LogRecord> Records => _records.ToArray();

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);

    public void Dispose() { }

    private sealed class Sink(TestLogSink owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes?.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            owner._entries.Enqueue((logLevel, message));
            var values = new Dictionary<string, object?>();
            owner._scopes?.ForEachScope((scope, v) => Flatten(scope, v), values);
            Flatten(state, values);
            owner._records.Enqueue(new LogRecord(logLevel, category, eventId, message, values));
        }

        private static void Flatten(object? source, Dictionary<string, object?> into)
        {
            if (source is IEnumerable<KeyValuePair<string, object?>> pairs)
                foreach (var (k, v) in pairs) into[k] = v;
        }
    }
}
