using DynamicModules.Domain.Data;
using DynamicModules.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>SQLite 的 mmap_size 是設定項（Sqlite:MmapSizeMb），預設關閉；開啟時每條連線都要套用（PRAGMA 是逐連線的）。</summary>
public class MmapSizeSettingTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"dm-mmap-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_file) + "*")) File.Delete(f);
    }

    private ServiceProvider Build(string? mmapMb)
    {
        var settings = new Dictionary<string, string?> { ["ConnectionStrings:Dynamic"] = $"Data Source={_file}" };
        if (mmapMb is not null) settings["Sqlite:MmapSizeMb"] = mmapMb;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddPersistence(config);
        return services.BuildServiceProvider();
    }

    private static async Task<long> MmapSizeOfAFreshConnection(IServiceProvider sp)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        await db.Database.OpenConnectionAsync();
        cmd.CommandText = "PRAGMA mmap_size";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task NotConfigured_MmapStaysOff()
    {
        await using var sp = Build(null);
        (await MmapSizeOfAFreshConnection(sp)).Should().Be(0);
    }

    [Fact]
    public async Task Configured_EveryConnectionGetsTheSize()
    {
        await using var sp = Build("64");
        const long expected = 64L * 1024 * 1024;
        (await MmapSizeOfAFreshConnection(sp)).Should().Be(expected);
        (await MmapSizeOfAFreshConnection(sp)).Should().Be(expected, "第二個 scope 是另一條連線，也要套用");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public async Task ZeroOrNegative_MeansOff(string value)
    {
        await using var sp = Build(value);
        (await MmapSizeOfAFreshConnection(sp)).Should().Be(0);
    }
}
