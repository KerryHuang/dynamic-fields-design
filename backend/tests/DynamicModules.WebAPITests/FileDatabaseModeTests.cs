using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace DynamicModules.WebAPITests;

/// <summary>檔案模式的測試資料庫要和正式站一樣是 WAL；rollback journal 下長交易會擋住其他連線的讀取（TestApp 不替產品設 WAL，由啟動初始化的 IDatabaseStartupTuning 負責，見 #20）。</summary>
[Trait("Category", "SqliteOnly")]
public class FileDatabaseModeTests
{
    [Fact]
    public void FileDatabase_IsWal_AfterHostStarts()
    {
        using var app = new TestApp(fileDatabase: true);
        app.CreateClient();

        using var connection = new SqliteConnection(app.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode";

        cmd.ExecuteScalar().Should().Be("wal");
    }

    [Fact]
    public void ExistingEmptyDatabaseFile_IsWal_AfterHostStarts()
    {
        // 部署時檔案已存在（空的 .db）：EF Migrate 不會設 WAL，啟動初始化要補上
        var file = Path.Combine(Path.GetTempPath(), $"dm-empty-{Guid.NewGuid():N}.db");
        File.Create(file).Dispose();
        try
        {
            using var app = new TestApp(sharedConnectionString: $"Data Source={file}");
            app.CreateClient();

            using var connection = new SqliteConnection($"Data Source={file}");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode";
            cmd.ExecuteScalar().Should().Be("wal");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(file) + "*")) File.Delete(f);
        }
    }
}
