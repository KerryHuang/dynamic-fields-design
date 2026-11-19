using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using DynamicModules.Application.Dynamic.OpsService.Alerts;
using DynamicModules.Application.Dynamic.OpsService.Health;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.TestSupport;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DynamicModules.WebAPITests.Ops;

/// <summary>o2c 測試共用：假寄送器、設定通道、讀寄送佇列、跑一輪寄送者。</summary>
internal static class AlertKit
{
    public const string Channel = OpsIncidentKit.Base + "/channels/smtp";

    /// <summary>SMTP 換成假寄送器（不連外網、不寄真信）。</summary>
    public static void UseFakeMail(IServiceCollection s, FakeMailSender sender)
    {
        s.RemoveAll<IAlertMailSender>();
        s.AddSingleton<IAlertMailSender>(sender);
    }

    public static TestApp App(FakeMailSender sender, bool noLock = false) =>
        new() { WithoutWriteSerialization = noLock, ExtraServices = s => UseFakeMail(s, sender) };

    /// <summary>經 API 存一份可寄信的通道設定；<paramref name="change"/> 改其中幾個欄位。</summary>
    public static Dictionary<string, object?> Settings(Action<Dictionary<string, object?>>? change = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["enabled"] = true, ["host"] = "smtp.test.invalid", ["port"] = 587, ["security"] = "startTls", ["userName"] = "ops",
            ["password"] = "s3cret-pw", ["fromAddress"] = "ops@test.invalid", ["fromName"] = "Formwright",
            ["toAddresses"] = new[] { "a@test.invalid", "b@test.invalid" },
        };
        change?.Invoke(body);
        return body;
    }

    public static async Task Configure(TestApp app, Action<Dictionary<string, object?>>? change = null) =>
        (await app.AsUser().PutAsJsonAsync(Channel, Settings(change))).EnsureSuccessStatusCode();

    public static async Task<List<AlertOutbox>> Outbox(TestApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AlertOutbox>().AsNoTracking().OrderBy(r => r.Id).ToListAsync();
    }

    public static async Task<AlertChannel?> ChannelRow(TestApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AlertChannel>().AsNoTracking().SingleOrDefaultAsync();
    }

    /// <summary>跑一輪寄送者（不經 <c>ops.monitor</c> 的租約；正確性不靠租約，§17.7）。</summary>
    public static async Task<int> Dispatch(TestApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await app.Services.GetRequiredService<AlertDispatcher>()
            .RunAsync(scope.ServiceProvider.GetRequiredService<ISender>(), () => null, CancellationToken.None);
    }

    /// <summary>一件基礎設施警告事件（健康檢查類，不會被自動結案）。</summary>
    public static IncidentSignal Infra(string fingerprint, DateTime occurredUtc, IncidentSeverity severity = IncidentSeverity.Warning) => new()
    {
        Fingerprint = fingerprint, Source = IncidentSource.Health, Category = IncidentCategory.Infra, Severity = severity,
        Code = HealthCheckCatalog.StorageDiskFree, TitleKey = IncidentTitles.HealthPrefix + HealthCheckCatalog.StorageDiskFree,
        TitleParams = IncidentTitles.Health("warning", 15, 20, 10, 1), NormalizedMessage = "secret-normalized-message", OccurredUtc = occurredUtc,
        Version = "test",
    };

    public static int SentOrSendingWithinHour(IEnumerable<AlertOutbox> rows, DateTime nowUtc) =>
        rows.Count(r => r.Transition != AlertTransition.Digest
            && (r.Status == AlertOutboxStatus.Sending || (r.Status == AlertOutboxStatus.Sent && r.SentUtc >= nowUtc.AddHours(-1))));

    /// <summary>一個保證沒人聽的本機埠（綁定後立刻關掉）。</summary>
    public static int ClosedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

/// <summary>假的 SMTP 寄送器：記下每次寄送（含失敗）；<see cref="Result"/> 決定結果，預設成功。</summary>
internal sealed class FakeMailSender : IAlertMailSender
{
    public sealed record Call(SmtpSettings Settings, string? Password, AlertMail Mail, MailSendResult Result);

    private readonly ConcurrentQueue<Call> _calls = new();

    public Func<AlertMail, MailSendResult>? Result { get; set; }

    public IReadOnlyList<Call> Calls => [.. _calls];

    public IReadOnlyList<AlertMail> Sent => [.. _calls.Where(c => c.Result.Ok).Select(c => c.Mail)];

    public Task<MailSendResult> SendAsync(SmtpSettings settings, string? password, AlertMail mail, CancellationToken ct)
    {
        var result = Result?.Invoke(mail) ?? MailSendResult.Success;
        _calls.Enqueue(new Call(settings, password, mail, result));
        return Task.FromResult(result);
    }
}

/// <summary>
/// 模擬資料庫空間滿（C13）：開啟時每個寫入指令（INSERT／UPDATE／DELETE）丟各 provider 真的「空間滿」例外
/// （SQLite 錯誤碼 13、PostgreSQL <c>53100</c>），讀取照常。
/// </summary>
internal sealed class DiskFullSwitch : DbCommandInterceptor
{
    public volatile bool On;

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Check(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken ct = default)
    {
        Check(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Check(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken ct = default)
    {
        Check(command);
        return ValueTask.FromResult(result);
    }

    private void Check(DbCommand command)
    {
        if (!On) return;
        var text = command.CommandText.TrimStart();
        if (!(text.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) || text.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)))
            return;
        throw TestDatabase.IsPostgres
            ? new Npgsql.PostgresException("could not extend file", "ERROR", "ERROR", "53100")
            : new SqliteException("database or disk is full", 13);
    }
}

/// <summary>
/// 不支援 STARTTLS 的假 SMTP 伺服器（D10、§17.10.6）：EHLO 能力沒有 STARTTLS，記下收到的每一行指令；
/// 收得下 MAIL／RCPT／DATA（給加密方式 <c>none</c> 的內網 relay 情境）。只聽本機埠，不連外網。
/// </summary>
internal sealed class NoStartTlsSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public NoStartTlsSmtpServer()
    {
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public IReadOnlyList<string> Lines => [.. _lines];

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 fake.test ESMTP");
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    _lines.Enqueue(line);
                    var verb = line.Split(' ')[0].ToUpperInvariant();
                    if (verb is "EHLO") await writer.WriteAsync("250-fake.test\r\n250-AUTH PLAIN LOGIN\r\n250 8BITMIME\r\n");
                    else if (verb is "HELO") await writer.WriteLineAsync("250 fake.test");
                    else if (verb is "MAIL" or "RCPT") await writer.WriteLineAsync("250 ok");
                    else if (verb is "DATA")
                    {
                        await writer.WriteLineAsync("354 go ahead");
                        while (await reader.ReadLineAsync(_stop.Token) is { } data && data != ".") _lines.Enqueue(data);
                        await writer.WriteLineAsync("250 queued");
                    }
                    else if (verb is "QUIT")
                    {
                        await writer.WriteLineAsync("221 bye");
                        break;
                    }
                    else await writer.WriteLineAsync("502 not implemented");
                }
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (Exception)
        {
            // 關閉時的例外不影響測試結果
        }
    }
}
