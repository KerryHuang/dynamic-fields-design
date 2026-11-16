using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace DynamicModules.WebAPITests;

/// <summary>檔案模式的測試資料庫要和正式站一樣是 WAL；rollback journal 下長交易會擋住其他連線的讀取（見 TestApp.UseWal）。</summary>
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
}
