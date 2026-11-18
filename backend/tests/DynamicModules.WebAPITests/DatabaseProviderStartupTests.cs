using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.WebAPITests;

/// <summary>
/// 啟動時的 Database:Provider 檢查與 DataProtection:KeysDirectory（PostgreSQL provider 設計 §5.1、§5.8）。
/// PostgreSQL 真的啟動（建表、目標資料庫不存在時拒絕、兩個實例同時啟動）要有伺服器，pg0c 的測試環境前以本機走查驗證。
/// </summary>
public class DatabaseProviderStartupTests
{
    [Fact]
    public void UnsupportedProvider_RefusesToStart()
    {
        using var app = new TestApp(seedDemoPeople: false);
        app.Settings["Database:Provider"] = "SqlServer";

        var act = () => app.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*SqlServer*");
    }

    [Fact]
    public void Postgres_WithSqlOnlyReports_RefusesToStart()
    {
        // 報表 SQL 路徑在 pg1 才支援 PostgreSQL（設計 §5.9）；在連資料庫之前就拒絕
        using var app = new TestApp(seedDemoPeople: false);
        app.Settings["Database:Provider"] = "Postgres";
        app.Settings["Reports:Engine"] = "SqlOnly";

        var act = () => app.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*SqlOnly*");
    }

    // SqliteOnly：驗的是明寫 Provider=Sqlite 時照舊啟動；PostgreSQL 模式的測試主機給的是 PostgreSQL 連線字串，配 Sqlite provider 沒有意義
    [Fact]
    [Trait("Category", "SqliteOnly")]
    public void ExplicitSqlite_StartsAsBefore()
    {
        using var app = new TestApp(seedDemoPeople: false);
        app.Settings["Database:Provider"] = "Sqlite";

        app.CreateClient();
    }

    [Fact]
    public void KeysDirectory_WhenConfigured_KeysAreWrittenThere()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dm-keys-{Guid.NewGuid():N}");
        try
        {
            using var app = new TestApp(seedDemoPeople: false);
            app.Settings["DataProtection:KeysDirectory"] = dir;
            app.CreateClient();

            app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("x");

            Directory.GetFiles(dir, "key-*.xml").Should().NotBeEmpty();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
