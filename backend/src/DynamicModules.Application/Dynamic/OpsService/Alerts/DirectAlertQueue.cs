using DynamicModules.Application.Dynamic.OpsService.Health;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.OpsService.Alerts;

/// <summary>
/// 資料庫寫不進時的直送佇列（維運設計 §17.2.6、O2-T8；singleton，只在本實例記憶體）：健康檢查結果寫不進資料庫時，
/// 開啟與升級的轉換改由看門狗的寄送迴圈直接寄（不經 <c>ops.monitor</c>：資料庫不可寫時排程器搶不到租約）。
/// 同一個 <c>TransitionId</c> 只寄一次；重試 1、5、15、60 分鐘、最多 5 次；每小時上限以本實例記憶體計數，超過的不寄、只記 Warning。
/// 通道設定與寄信政策讀不到時用最後一次成功讀到的（密碼仍是加密字串，寄送時才解；信件語系也用那份）；啟動後從沒讀到過就寄不出去。
/// 已直送的識別碼留著給補寫對照（佇列列記成 <c>sentDirect</c>，不再寄）；同一檢查之後回到正常時，還沒寄出的開啟、升級信撤掉（已過時）。
/// </summary>
public sealed class DirectAlertQueue(
    IServiceScopeFactory scopes, IAlertMailSender sender, IAlertSecretProtector protector, IOpsEnvironment env, AlertMailOptions options,
    TimeProvider time, ILogger<DirectAlertQueue> logger)
{
    /// <summary>直送佇列上限（技術限制，§17.9）。</summary>
    public const int MaxItems = 20;
    /// <summary>已直送識別碼的保留筆數：與待補寫佇列同一個上限（補寫只會對照得到還在待補寫佇列裡的轉換）。</summary>
    public const int MaxSentRecords = HealthCheckEngine.MaxPending;

    private sealed class Item(string transitionId, string code, AlertMail mail, DateTime nextAttemptUtc)
    {
        public string TransitionId { get; } = transitionId;
        public string Code { get; } = code;
        public AlertMail Mail { get; } = mail;
        public int Attempts { get; set; }
        public DateTime NextAttemptUtc { get; set; } = nextAttemptUtc;
    }

    private readonly object _sync = new();
    private readonly List<Item> _items = [];
    private readonly Dictionary<string, DateTime> _sent = new(StringComparer.Ordinal);
    private readonly Queue<string> _sentOrder = new();
    private readonly Queue<DateTime> _sentTimes = new();
    private readonly SemaphoreSlim _sending = new(1, 1);
    private SmtpSettings? _settings;
    private AlertEmailPolicy _policy = AlertEmailPolicy.Factory;

    /// <summary>還沒寄出的直送信（依順序，給監控與測試）。</summary>
    public IReadOnlyList<string> PendingTransitionIds()
    {
        lock (_sync) return [.. _items.Select(i => i.TransitionId)];
    }

    /// <summary>直送已寄出的時間；沒直送過回 null。</summary>
    public DateTime? SentUtc(string transitionId)
    {
        lock (_sync) return _sent.TryGetValue(transitionId, out var at) ? at : null;
    }

    /// <summary>補寫已把這個轉換寫進佇列（之後由佇列寄）：撤掉還沒寄出的直送信。</summary>
    public void Withdraw(string transitionId)
    {
        lock (_sync) _items.RemoveAll(i => i.TransitionId == transitionId);
    }

    /// <summary>
    /// 一筆寫不進資料庫的健康檢查結果：比本實例上一筆變糟（新開或升級）且在寄信政策內就排進直送；回到正常就撤掉同一檢查還沒寄出的信。
    /// </summary>
    public void Consider(HealthObservation o, HealthStatus? previous)
    {
        lock (_sync)
        {
            if (HealthCheckCatalog.Rank(o.Status) == 0)
            {
                if (o.Status == HealthStatus.Ok) _items.RemoveAll(i => i.Code == o.Code);
                return;
            }
            if (HealthCheckCatalog.Find(o.Code) is not { } def || _sent.ContainsKey(o.TransitionId) || _items.Any(i => i.TransitionId == o.TransitionId))
                return;
            var severity = SeverityOf(o.Status);
            var before = previous is { } p && HealthCheckCatalog.Rank(p) > 0 ? SeverityOf(p) : (IncidentSeverity?)null;
            AlertTransition transition;
            if (before is null) transition = AlertTransition.Opened;
            else if (severity > before) transition = AlertTransition.Escalated;
            else return;
            if (!_policy.Allows(def.Category, severity)) return;
            var mail = AlertMailComposer.Direct(IncidentTitles.HealthPrefix + def.Code,
                IncidentTitles.Health(HealthCheckCatalog.Camel(o.Status), o.Value, null, null, 1), severity, def.Category, transition,
                o.CheckedUtc, env.Version, _settings?.MailLocale, env.Edition, options);
            if (_items.Count >= MaxItems)
            {
                logger.LogWarning("直送佇列已滿（{Max} 封），丟掉最舊的一封：{TransitionId}", MaxItems, _items[0].TransitionId);
                _items.RemoveAt(0);
            }
            _items.Add(new Item(o.TransitionId, o.Code, mail, time.GetUtcNow().UtcDateTime));
        }
    }

    /// <summary>看門狗寄送迴圈的一輪：先重讀通道設定與政策（讀不到沿用上一次），再寄到期的信；寄送在交易外、單封逾時 30 秒。</summary>
    public async Task SendDueAsync(CancellationToken ct)
    {
        await RefreshAsync(ct);
        await _sending.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow().UtcDateTime;
            List<Item> due;
            lock (_sync) due = [.. _items.Where(i => i.NextAttemptUtc <= now)];
            foreach (var item in due)
            {
                if (ct.IsCancellationRequested) return;
                await SendOneAsync(item, ct);
            }
        }
        finally
        {
            _sending.Release();
        }
    }

    private async Task SendOneAsync(Item item, CancellationToken ct)
    {
        var settings = _settings;
        if (settings is null)
        {
            logger.LogWarning("直送信寄不出去：還沒讀到可用的告警通道設定（{TransitionId}）", item.TransitionId);
            return;
        }
        var now = time.GetUtcNow().UtcDateTime;
        lock (_sync)
        {
            while (_sentTimes.TryPeek(out var at) && at < now - AlertLimits.QuotaWindow) _sentTimes.Dequeue();
            if (_sentTimes.Count >= _policy.MaxPerHour)
            {
                logger.LogWarning("直送信超過每小時上限 {Max} 封，不寄：{TransitionId}", _policy.MaxPerHour, item.TransitionId);
                _items.Remove(item);
                return;
            }
        }
        string? password = null;
        if (settings.PasswordProtected is { } protectedText && (password = protector.TryUnprotect(protectedText)) is null)
        {
            logger.LogError("直送信寄不出去：SMTP 密碼解不開（金鑰可能不見了），請重填（{TransitionId}）", item.TransitionId);
            lock (_sync) _items.Remove(item);
            return;
        }
        MailSendResult result;
        try
        {
            using var timeout = new CancellationTokenSource(AlertLimits.SendTimeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            result = await sender.SendAsync(settings, password, item.Mail, linked.Token);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "直送信寄送丟例外（{TransitionId}）", item.TransitionId);
            result = new MailSendResult(false, MailSendResult.Send);
        }
        lock (_sync)
        {
            if (result.Ok)
            {
                _items.Remove(item);
                _sentTimes.Enqueue(now);
                _sent[item.TransitionId] = now;
                _sentOrder.Enqueue(item.TransitionId);
                while (_sentOrder.Count > MaxSentRecords) _sent.Remove(_sentOrder.Dequeue());
                return;
            }
            item.Attempts++;
            if (item.Attempts >= AlertLimits.MaxAttempts)
            {
                logger.LogError("直送信重試 {Attempts} 次仍失敗（{Error}），放棄：{TransitionId}", item.Attempts, result.Describe(), item.TransitionId);
                _items.Remove(item);
                return;
            }
            item.NextAttemptUtc = AlertLimits.NextAttempt(now, item.Attempts);
        }
        logger.LogWarning("直送信寄送失敗（{Error}），第 {Attempts} 次：{TransitionId}", result.Describe(), item.Attempts, item.TransitionId);
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var channel = await scope.ServiceProvider.GetRequiredService<IAlertChannelRepository>().GetAsync(AlertChannel.Smtp, ct);
            var policy = await AlertEmailPolicy.ReadAsync(scope.ServiceProvider.GetRequiredService<IPolicyReader>(), ct);
            lock (_sync)
            {
                _settings = SmtpSettings.From(channel);
                _policy = policy;
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "直送讀不到告警通道設定，{Fallback}", _settings is null ? "還沒有可用的設定" : "沿用上一次讀到的");
        }
    }

    private static IncidentSeverity SeverityOf(HealthStatus s) => s == HealthStatus.Critical ? IncidentSeverity.Critical : IncidentSeverity.Warning;
}
