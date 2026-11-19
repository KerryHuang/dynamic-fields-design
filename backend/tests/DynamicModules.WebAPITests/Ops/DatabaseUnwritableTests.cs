using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static DynamicModules.WebAPITests.Ops.OpsIncidentKit;

namespace DynamicModules.WebAPITests.Ops;

/// <summary>空間滿與連不上回 503（維運設計 §17.2.9、§17.8 C12）；例外用各 provider 真的型別建構。</summary>
public class DatabaseUnwritableTests
{
    /// <summary>存檔時丟指定的例外（一次）。</summary>
    private sealed class SaveFault : SaveChangesInterceptor
    {
        public Func<Exception>? Next;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken ct = default) => Interlocked.Exchange(ref Next, null) is { } make ? throw make() : ValueTask.FromResult(result);
    }

    /// <summary>下一個查詢丟指定的例外（一次）。</summary>
    private sealed class ReaderFault : DbCommandInterceptor
    {
        public Func<Exception>? Next;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default) =>
            Interlocked.Exchange(ref Next, null) is { } make ? throw make() : ValueTask.FromResult(result);

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) =>
            Interlocked.Exchange(ref Next, null) is { } make ? throw make() : result;
    }

    private static Exception Full() => TestDatabase.IsPostgres
        ? new DbUpdateException("x", new Npgsql.PostgresException("could not extend file", "ERROR", "ERROR", "53100"))
        : new DbUpdateException("x", new SqliteException("database or disk is full", 13));

    private static Exception Unavailable() => TestDatabase.IsPostgres
        ? new Npgsql.NpgsqlException("連不到主機", new SocketException((int)SocketError.ConnectionRefused))
        : new SqliteException("unable to open database file", 14);

    private static async Task<string?> Code(HttpResponseMessage res) => (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    [Fact]
    public async Task C12_DiskFullOnSave_503Unwritable_SessionReadUnavailable_503Unavailable_NoDefectIncident()
    {
        var save = new SaveFault();
        var reader = new ReaderFault();
        using var app = new TestApp();
        app.Interceptors.Add(save);
        app.Interceptors.Add(reader);
        var client = app.AsUser();
        (await client.GetAsync($"{Base}/overview")).EnsureSuccessStatusCode();

        save.Next = Full;
        var full = await client.PutAsJsonAsync("/api/v3/dynamic/system/policies/ops.clientErrorCollection", new { value = false });
        full.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        full.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(60));
        (await Code(full)).Should().Be("databaseUnwritable");

        reader.Next = Unavailable;
        var down = await client.GetAsync($"{Base}/overview");
        down.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        down.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(60));
        (await Code(down)).Should().Be("databaseUnavailable");

        app.Logs.Records.Should().Contain(r => r.EventId.Id == 5003).And.NotContain(r => r.EventId.Id == 5000, "不算 500");
        await app.Services.GetRequiredService<IncidentInboxDrain>().FlushAsync(CancellationToken.None);
        (await Incidents(app)).Should().BeEmpty("同一原因由 database.writable 開事件，不開產品缺陷");
    }
}
