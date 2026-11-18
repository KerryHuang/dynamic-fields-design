using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.WebAPITests;

/// <summary>啟動時的 Database:Provider 檢查與 DataProtection:KeysDirectory（PostgreSQL provider 設計 §5.1、§5.8）。</summary>
public class DatabaseProviderStartupTests
{
    [Theory]
    [InlineData("Postgres", "*尚未提供*")]
    [InlineData("SqlServer", "*SqlServer*")]
    public void UnsupportedProvider_RefusesToStart(string provider, string message)
    {
        using var app = new TestApp(seedDemoPeople: false);
        app.Settings["Database:Provider"] = provider;

        var act = () => app.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage(message);
    }

    [Fact]
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
