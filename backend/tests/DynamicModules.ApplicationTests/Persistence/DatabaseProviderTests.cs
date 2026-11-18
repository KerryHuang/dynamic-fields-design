using DynamicModules.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>Database:Provider 的解析與資料庫檔案目錄（PostgreSQL provider 設計 §5.1、§5.8）。</summary>
public class DatabaseProviderTests
{
    private static IConfiguration Config(string? provider) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Provider"] = provider }).Build();

    [Theory]
    [InlineData(null, DatabaseProvider.Sqlite)]
    [InlineData("", DatabaseProvider.Sqlite)]
    [InlineData("Sqlite", DatabaseProvider.Sqlite)]
    [InlineData("sqlite", DatabaseProvider.Sqlite)]
    [InlineData("Postgres", DatabaseProvider.Postgres)]
    [InlineData(" POSTGRES ", DatabaseProvider.Postgres)]
    public void Read_AcceptsSqliteAndPostgres(string? value, DatabaseProvider expected) =>
        DatabaseProviders.Read(Config(value)).Should().Be(expected);

    [Theory]
    [InlineData("SqlServer")]
    [InlineData("PostgreSQL")]   // 不猜同義字，只認設計列的兩個值
    public void Read_RejectsOtherValues(string value)
    {
        var act = () => DatabaseProviders.Read(Config(value));
        act.Should().Throw<InvalidOperationException>().WithMessage($"*{value}*");
    }

    [Fact]
    public void FileDirectory_SqliteFileIsItsDirectory_MemoryAndPostgresHaveNone()
    {
        var file = Path.Combine(Path.GetTempPath(), "fw", "app.db");
        DatabaseProviders.FileDirectory(DatabaseProvider.Sqlite, $"Data Source={file}").Should().Be(Path.GetDirectoryName(file));
        DatabaseProviders.FileDirectory(DatabaseProvider.Sqlite, "Data Source=:memory:").Should().BeNull();
        DatabaseProviders.FileDirectory(DatabaseProvider.Sqlite, "Data Source=file:x?mode=memory&cache=shared").Should().BeNull();
        DatabaseProviders.FileDirectory(DatabaseProvider.Postgres, "Host=localhost;Database=fw").Should().BeNull();
    }
}
