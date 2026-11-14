using DynamicModules.Application;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Acting;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.AttachmentService;
using DynamicModules.ApplicationTests.Persistence;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Files;
using DynamicModules.Persistence.Seed;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

/// <summary>Demo 產生器測試用：已種子的 SQLite、admin 身分、固定營運時間，最後掛上 AddActingOverrides。</summary>
public sealed class DemoTestHost : IAsyncDisposable
{
    public static readonly DateOnly Today = new(2026, 11, 5);

    private SqliteTestDb _db = null!;
    private ServiceProvider _sp = null!;
    private readonly string _attachmentRoot = Path.Combine(Path.GetTempPath(), $"dm-demo-att-{Guid.NewGuid():N}");

    public FakeUser User { get; } = new();

    public SqliteTestDb Db => _db;

    public IServiceProvider Services => _sp;

    public sealed class FakeUser : ICurrentUser { public string UserId { get; set; } = "admin"; }

    private sealed class FakeTime(DateOnly today) : IBusinessTime
    {
        public DateTime Now => today.ToDateTime(new TimeOnly(10, 0));
        DateOnly IBusinessTime.Today => DateOnly.FromDateTime(Now);
    }

    /// <param name="today">營運時間的今天（10:00）；預設 <see cref="Today"/>。</param>
    /// <param name="configure">在 SP 建好前追加／覆寫註冊（最後註冊者勝出）。</param>
    public static async Task<DemoTestHost> CreateAsync(DateOnly? today = null, Action<IServiceCollection>? configure = null)
    {
        var host = new DemoTestHost { _db = await SqliteTestDb.CreateAsync() };
        await SeedData.EnsureSeededAsync(host._db.Context);
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddPersistenceRepositories();
        services.AddScoped(_ => host._db.Context);
        services.AddScoped<ITransactionManager, EfTransactionManager>();
        services.AddScoped<IWorkSavepoints, EfWorkSavepoints>();
        services.AddSingleton<ICurrentUser>(host.User);
        services.AddSingleton<IBusinessTime>(new FakeTime(today ?? Today));
        services.AddSingleton(new AuthSettings(false));
        services.AddSingleton<DynamicModules.Application.Dynamic.TaskService.ITaskUndoTokens, DynamicModules.ApplicationTests.Dynamic.Tasks.TestTaskUndoTokens>();
        // 情境會在任務上留言（不帶附件），留言的處理器需要附件服務
        services.AddSingleton(new AttachmentSettings(host._attachmentRoot, true, null));
        services.AddSingleton<IAttachmentStore>(new LocalAttachmentStore(host._attachmentRoot));
        services.AddActingOverrides();
        configure?.Invoke(services);
        host._sp = services.BuildServiceProvider();
        return host;
    }

    public Task<TRes> Send<TRes>(IRequest<TRes> request) => _sp.GetRequiredService<IMediator>().Send(request);

    public async ValueTask DisposeAsync()
    {
        await _sp.DisposeAsync();
        await _db.DisposeAsync();
        if (Directory.Exists(_attachmentRoot)) Directory.Delete(_attachmentRoot, recursive: true);
    }
}
