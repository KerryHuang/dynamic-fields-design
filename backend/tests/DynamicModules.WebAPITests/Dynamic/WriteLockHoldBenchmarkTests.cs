using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using DynamicModules.Domain.Data;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 單筆新增的持鎖時間（PostgreSQL provider 設計 §5.6「代價」）：寫入交易從開始（PostgreSQL 已取得全庫鎖、SQLite 已 BEGIN IMMEDIATE）到提交的時間，
/// 等於寫入吞吐上限的倒數。兩種資料庫都量（看 FORMWRIGHT_TEST_DB），結果印在測試輸出，寫回設計 §5.6。
/// 預設不跑：<c>--filter "FullyQualifiedName~WriteLockHoldBenchmarkTests"</c> 會覆蓋 csproj 的 Category!=Benchmark。
/// </summary>
[Trait("Category", "Benchmark")]
public class WriteLockHoldBenchmarkTests(ITestOutputHelper output)
{
    private const int Warmup = 5;
    private const int Measured = 40;

    [Fact]
    public async Task SingleCreate_TransactionDuration()
    {
        var timer = new TransactionTimer();
        using var app = new TestApp(fileDatabase: true);
        app.Settings["Logging:LogLevel:Default"] = "Warning";
        app.ExtraServices = s => AddInterceptor(s, timer);
        var admin = app.AsUser();
        var module = await CreateModule(admin);
        var fields = new List<string>();
        for (var i = 0; i < 4; i++) fields.Add((await AddField(admin, module, "text", new { isFilterable = true })).Key);
        fields.Add((await AddField(admin, module, "decimal", new { scale = 2, isFilterable = true })).Key);
        await Activate(admin, module);
        var title = await TitleKey(admin, module);

        async Task Create(int n)
        {
            var values = new Dictionary<string, object> { [title] = $"t{n}" };
            for (var i = 0; i < 4; i++) values[fields[i]] = $"v{n}-{i}";
            values[fields[4]] = n;
            var res = await admin.PostAsJsonAsync($"{Base}/modules/{module}/records", new { values });
            res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        }

        for (var n = 0; n < Warmup; n++) await Create(n);
        timer.Reset();
        var wall = Stopwatch.StartNew();
        for (var n = 0; n < Measured; n++) await Create(Warmup + n);
        wall.Stop();

        var ms = timer.Durations.Select(d => d.TotalMilliseconds).Order().ToArray();
        ms.Should().NotBeEmpty();
        var provider = TestDatabase.IsPostgres ? "PostgreSQL" : "SQLite";
        output.WriteLine($"{provider}：{Measured} 筆新增、{ms.Length} 個交易；持鎖（交易開始到提交）中位數 {ms[ms.Length / 2]:F1} ms、" +
                         $"P90 {ms[(int)(ms.Length * 0.9)]:F1} ms、最大 {ms[^1]:F1} ms；每筆請求平均 {wall.Elapsed.TotalMilliseconds / Measured:F1} ms");
    }

    /// <summary>在 TestApp 已包好的 DbContext 選項上再疊一個攔截器（同 TestApp.AddWriteGuard 的做法）。</summary>
    private static void AddInterceptor(IServiceCollection services, IInterceptor interceptor)
    {
        var original = services.Single(d => d.ServiceType == typeof(DbContextOptions<DynamicDbContext>));
        services.Remove(original);
        services.Add(new ServiceDescriptor(typeof(DbContextOptions<DynamicDbContext>), sp =>
            new DbContextOptionsBuilder<DynamicDbContext>((DbContextOptions<DynamicDbContext>)original.ImplementationFactory!(sp))
                .AddInterceptors(interceptor)
                .Options, original.Lifetime));
    }

    /// <summary>
    /// 記每個交易從 TransactionStarted 到提交的時間。註冊在 provider 的攔截器之後，所以 PostgreSQL 上起點是「已取得全庫鎖」。
    /// </summary>
    private sealed class TransactionTimer : DbTransactionInterceptor
    {
        private readonly ConcurrentDictionary<DbTransaction, long> _started = new();
        private readonly ConcurrentQueue<TimeSpan> _durations = new();

        public IReadOnlyCollection<TimeSpan> Durations => _durations.ToArray();

        public void Reset() => _durations.Clear();

        public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData,
            DbTransaction result, CancellationToken cancellationToken = default)
        {
            _started[result] = Stopwatch.GetTimestamp();
            return ValueTask.FromResult(result);
        }

        public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
        {
            _started[result] = Stopwatch.GetTimestamp();
            return result;
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Record(transaction);
            return Task.CompletedTask;
        }

        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Record(transaction);

        private void Record(DbTransaction transaction)
        {
            if (_started.TryRemove(transaction, out var start)) _durations.Enqueue(Stopwatch.GetElapsedTime(start));
        }
    }
}
