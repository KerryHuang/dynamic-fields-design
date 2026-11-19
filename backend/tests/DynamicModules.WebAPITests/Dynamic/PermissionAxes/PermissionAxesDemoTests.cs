using System.Net;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Acting;
using DynamicModules.Application.Dynamic.DemoService.Commands.GenerateDemoData;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using static DynamicModules.WebAPITests.Dynamic.PermissionAxes.PaxWorld;

namespace DynamicModules.WebAPITests.Dynamic.PermissionAxes;

/// <summary>
/// 產生 Demo 資料要權限設定（M4，PAX-12；權限軸設計 §7.3）。背景的 GenerateDemoDataRequest 進管線後在交易內被攔住、放行時丟例外，
/// 不跑完整產生（完整產生的行為由 DemoDataApiTests 涵蓋）。和其他 Demo 類測試一樣獨佔執行。
/// </summary>
[Collection(DemoGenerationCollection.Name)]
public sealed class PermissionAxesDemoTests : IAsyncLifetime
{
    private const string Status = $"{Base}/demo-data";
    private const string Generate = $"{Base}/demo-data/generate";

    /// <summary>攔住背景產生：進來時通知，放行時丟例外讓整批回滾（不真的產生）。</summary>
    private sealed class Block
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BlockBehavior<TReq, TRes>(Block block) : IPipelineBehavior<TReq, TRes> where TReq : notnull
    {
        public async Task<TRes> Handle(TReq request, RequestHandlerDelegate<TRes> next, CancellationToken ct)
        {
            if (request is not GenerateDemoDataRequest) return await next();
            block.Entered.TrySetResult();
            await block.Release.Task;
            throw new OperationCanceledException("PAX-12：測試攔住背景產生");
        }
    }

    private sealed class App() : TestApp(seedDemoPeople: false)
    {
        public Block Block { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(s =>
            {
                s.AddSingleton(Block);
                s.AddTransient(typeof(IPipelineBehavior<,>), typeof(BlockBehavior<,>));
            });
        }

        protected override void Dispose(bool disposing)
        {
            Block.Release.TrySetResult();
            base.Dispose(disposing);
        }
    }

    private readonly App _app = new();
    private PaxWorld _w = null!;

    public async Task InitializeAsync() => _w = await CreateAsync(_app, modules: false);

    public Task DisposeAsync()
    {
        _app.Dispose();
        return Task.CompletedTask;
    }

    private async Task<string?> State() => (await JsonOf(await _w.As(Admin).GetAsync(Status))).GetProperty("state").GetString();

    [Fact]
    public async Task PAX12_GenerateDemoData_NeedsPermissionAdmin()
    {
        await PaxAssert.Forbidden(await _w.As(Wang).PostAsync(Generate, null), "permissionAdminRequired");
        (await State()).Should().Be("notGenerated");

        // 直接以小王身分送背景請求：403、沒有任何寫入
        var guard = _w.GuardRows();
        var modules = _w.Db(db => db.Set<Module>().Count());
        using (var scope = _app.Services.CreateScope())
        using (ActingScope.Begin(Wang))
        {
            var r = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new GenerateDemoDataRequest(DateOnly.FromDateTime(DateTime.Today)));
            r.IsForbidden.Should().BeTrue();
        }
        _app.Block.Entered.Task.IsCompleted.Should().BeFalse("被 behavior 擋下，沒有進到交易");
        _w.GuardRows().Should().Equal(guard);
        _w.Db(db => db.Set<Module>().Count()).Should().Be(modules);

        var chen = await _w.As(Chen).PostAsync(Generate, null);
        chen.StatusCode.Should().Be(HttpStatusCode.OK, await chen.Content.ReadAsStringAsync());
        await _app.Block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        (await State()).Should().Be("running");
    }
}
