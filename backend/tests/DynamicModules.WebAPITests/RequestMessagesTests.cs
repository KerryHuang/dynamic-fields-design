using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.WebAPI.Errors;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DynamicModules.WebAPITests;

/// <summary>例外處理與 middleware 的訊息（語系包 i4c 計畫判斷 6）：取不到 services 時用內建繁中，不往外丟例外。純記憶體。</summary>
public class RequestMessagesTests
{
    private sealed record FixedLocale(string Locale) : ICurrentLocale;

    /// <summary>改表前固定的範本基準檔（ApplicationTests/Common/ServerTextBaseline）：鍵 → 語系 → 範本。</summary>
    internal static Dictionary<string, Dictionary<string, string>> Baseline(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "shared", "locale-chain-cases.json"))) dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("找不到 repo 根目錄");
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(Path.Combine(
            dir.FullName, "backend", "tests", "DynamicModules.ApplicationTests", "Common", "ServerTextBaseline", file)))!;
    }

    private static readonly Dictionary<string, string> Forbidden = Baseline("message-report.json")[MessageText.Forbidden];

    [Fact]
    public void NoRequestServices_BuiltinZh()
    {
        var ctx = new DefaultHttpContext();
        ctx.RequestServices.Should().BeNull();
        RequestMessages.Text(ctx, MessageText.Forbidden).Should().Be(Forbidden["zh-TW"]);
    }

    [Fact]
    public void DisposedServices_BuiltinZh()
    {
        var root = new ServiceCollection().AddScoped<ICurrentLocale>(_ => new FixedLocale("en-US")).BuildServiceProvider();
        var scope = root.CreateScope();
        var services = scope.ServiceProvider;
        scope.Dispose();
        var ctx = new DefaultHttpContext { RequestServices = services };

        RequestMessages.Text(ctx, MessageText.Forbidden).Should().Be(Forbidden["zh-TW"]);
    }

    [Fact]
    public void WithServices_UsesRequestLocale()
    {
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var registry = new LocaleRegistry(scopes, TimeProvider.System, NullLogger<LocaleRegistry>.Instance);
        var policy = new CachedSystemPolicy(scopes, TimeProvider.System, PolicyEnvironmentDefaults.Empty, registry, NullLogger<CachedSystemPolicy>.Instance);
        var services = new ServiceCollection()
            .AddSingleton(registry).AddSingleton(policy)
            .AddSingleton<ICurrentLocale>(new FixedLocale("en-US"))
            .AddSingleton<RequestLocale>()
            .AddSingleton<IMessageCatalog, MessageCatalog>()
            .BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };

        RequestMessages.Text(ctx, MessageText.Forbidden).Should().Be(Forbidden["en-US"]);
    }
}
