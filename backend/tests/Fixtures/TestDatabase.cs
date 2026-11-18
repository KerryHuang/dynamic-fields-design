using DynamicModules.Domain.Data;
using DynamicModules.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace DynamicModules.TestSupport;

/// <summary>
/// 測試用的資料庫種類（PostgreSQL provider 設計 §6.3）：環境變數 <c>FORMWRIGHT_TEST_DB=postgres</c> 時共用的測試基礎（TestDb、TestApp）改用 PostgreSQL，
/// 沒設就是 SQLite。ApplicationTests 與 WebAPITests 共用（Compile Include）。
/// </summary>
public static class TestDatabase
{
    public const string ProviderVariable = "FORMWRIGHT_TEST_DB";

    public static bool IsPostgres { get; } =
        string.Equals(Environment.GetEnvironmentVariable(ProviderVariable)?.Trim(), "postgres", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// PostgreSQL 測試伺服器與樣板資料庫（PostgreSQL provider 設計 §6.1、§6.2）：
/// <list type="bullet">
/// <item>有 <c>FORMWRIGHT_TEST_PG</c>（可以 CREATE DATABASE 的連線字串）就用它；否則以 Testcontainers 起 <c>postgres:16-alpine</c>，
/// 一個測試行程一個容器，跑完由 Testcontainers 的資源回收容器清掉。</item>
/// <item>第一次用到時建樣板資料庫並套 PostgreSQL migration（只有 schema，種子照舊由測試主機啟動時寫），之後每個測試
/// <c>CREATE DATABASE … TEMPLATE</c> 一份、結束時 <c>DROP … WITH (FORCE)</c>。</item>
/// </list>
/// 樣板名稱每個行程不同：外部伺服器可能有別的測試行程同時在用，也可能留著上次的舊樣板。
/// </summary>
public static class PostgresTestServer
{
    public const string ServerVariable = "FORMWRIGHT_TEST_PG";

    /// <summary>測試用映像（設計 §11 第 8 項，可寫死）。gate 事先 docker pull 同一個標籤。</summary>
    public const string Image = "postgres:16-alpine";

    /// <summary>
    /// 只用在測試容器的伺服器參數（設計 §6.1、§11 第 8 項）：連線數應付 xUnit 平行跑時的多個連線池；
    /// 後三個省掉建庫、提交時的落盤，資料可拋棄才能這樣設，正式環境絕不能用。外部伺服器不套用。
    /// Testcontainers 的 PostgreSQL 模組預設也帶後三個；這裡明寫，不依賴套件預設。
    /// </summary>
    private static readonly string[] ContainerArgs =
        ["-c", "max_connections=300", "-c", "fsync=off", "-c", "synchronous_commit=off", "-c", "full_page_writes=off"];

    /// <summary>
    /// 複製樣板的策略（設計 §6.2）。兩種都實測過（結果寫在 §6.5）：樣板約 11 MB，測試容器關了 fsync，兩者每個都約 12–13 ms、差在誤差內；
    /// 留 PostgreSQL 的預設 <c>WAL_LOG</c>，<c>FILE_COPY</c> 前後各強制一次 checkpoint，平行建庫時會互相等。
    /// </summary>
    private const string CloneStrategy = "WAL_LOG";

    private static readonly string TemplateName = $"fw_template_{Guid.NewGuid():N}"[..24];
    // Task.Run：在執行緒池上跑、不抓 xUnit 的 SynchronizationContext。TestApp 的建構函式同步等這個結果，
    // 若續傳要回 xUnit 的同步內容（執行緒數有限、都在同步等），平行跑時會整個卡死
    private static readonly Lazy<Task<string>> Admin = new(() => Task.Run(StartAsync), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>連到 <c>postgres</c> 系統資料庫的管理連線字串（建庫、刪庫、查 pg_stat_activity 用）。</summary>
    public static Task<string> AdminConnectionStringAsync() => Admin.Value;

    /// <summary>建一個測試資料庫並回傳它的連線字串；<paramref name="migrated"/> 為 false 時是空資料庫（不從樣板複製）。</summary>
    public static async Task<string> CreateDatabaseAsync(bool migrated = true)
    {
        var admin = await Admin.Value;
        var name = $"fw_{Guid.NewGuid():N}";
        var source = migrated ? $"TEMPLATE \"{TemplateName}\" STRATEGY {CloneStrategy}" : "TEMPLATE template0";
        await ExecuteAsync(admin, $"CREATE DATABASE \"{name}\" {source}");
        return new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
    }

    /// <summary>同步版本：給建構函式（TestApp）用。在執行緒池上跑，理由同 <see cref="Admin"/>。</summary>
    public static string CreateDatabase(bool migrated = true) => Task.Run(() => CreateDatabaseAsync(migrated)).GetAwaiter().GetResult();

    /// <summary>刪掉測試資料庫；先清掉它的連線池，<c>WITH (FORCE)</c> 再斷掉還開著的連線。</summary>
    public static async Task DropDatabaseAsync(string connectionString)
    {
        await using (var pooled = new NpgsqlConnection(connectionString)) NpgsqlConnection.ClearPool(pooled);
        var name = new NpgsqlConnectionStringBuilder(connectionString).Database;
        await ExecuteAsync(await Admin.Value, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
    }

    public static void DropDatabase(string connectionString) => Task.Run(() => DropDatabaseAsync(connectionString)).GetAwaiter().GetResult();

    private static async Task<string> StartAsync()
    {
        var admin = Environment.GetEnvironmentVariable(ServerVariable) is { Length: > 0 } external
            ? new NpgsqlConnectionStringBuilder(external) { Database = "postgres" }.ConnectionString
            : await StartContainerAsync();
        await BuildTemplateAsync(admin);
        return admin;
    }

    private static async Task<string> StartContainerAsync()
    {
        try
        {
            var container = new PostgreSqlBuilder(Image)
                .WithCommand(ContainerArgs)
                .Build();
            await container.StartAsync();
            // 不在這裡 Dispose：容器活到測試行程結束，由 Testcontainers 的資源回收容器清掉
            return new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = "postgres" }.ConnectionString;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"PostgreSQL 測試環境無法啟動：Docker 沒有在跑，或設定 {ServerVariable}（可以 CREATE DATABASE 的連線字串）。", ex);
        }
    }

    private static async Task BuildTemplateAsync(string admin)
    {
        await ExecuteAsync(admin, $"CREATE DATABASE \"{TemplateName}\"");
        var template = new NpgsqlConnectionStringBuilder(admin) { Database = TemplateName }.ConnectionString;
        await using (var db = new DynamicDbContext(
            (DbContextOptions<DynamicDbContext>)PostgresDatabase.Configure(new DbContextOptionsBuilder<DynamicDbContext>(), template,
                new PostgresWriteSettings(PostgresWriteSettings.DefaultLockTimeoutSeconds)).Options))
            await db.Database.MigrateAsync();
        // 樣板還有人連著就不能複製：清掉它的連線池
        await using (var pooled = new NpgsqlConnection(template)) NpgsqlConnection.ClearPool(pooled);
        if (Environment.GetEnvironmentVariable(ServerVariable) is { Length: > 0 })
            // 外部伺服器不會隨行程消失：盡力在行程結束時刪掉這個行程的樣板
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { ExecuteAsync(admin, $"DROP DATABASE IF EXISTS \"{TemplateName}\" WITH (FORCE)").GetAwaiter().GetResult(); }
                catch { /* 盡力而為 */ }
            };
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// 只在 PostgreSQL 有意義的案例（PostgreSQL provider 設計 §6.4）：<c>FORMWRIGHT_TEST_DB=postgres</c> 時才跑，SQLite 跑時略過。
/// 伺服器由 <see cref="PostgresTestServer"/> 提供（測試容器或 <c>FORMWRIGHT_TEST_PG</c>）。
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!TestDatabase.IsPostgres) Skip = $"只在 PostgreSQL 跑（{TestDatabase.ProviderVariable}=postgres）";
    }
}
