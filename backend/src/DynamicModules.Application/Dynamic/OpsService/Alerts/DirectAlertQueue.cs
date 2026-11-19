using DynamicModules.Application.Dynamic.OpsService.Health;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.OpsService.Alerts;

/// <summary>補寫或去重核對時，直送佇列對這個轉換的狀態（§17.13 審查修正「直送與補寫交接」）。</summary>
public enum DirectHandoff
{
    /// <summary>直送沒有這封、也沒寄過：照一般寫法。</summary>
    None,
    /// <summary>直送已寄出（<see cref="DirectReservation.SentUtc"/>）：佇列列記 <c>sentDirect</c>。</summary>
    Sent,
    /// <summary>還沒開始寄，已保留不再寄：提交後交給佇列。</summary>
    Queued,
    /// <summary>SMTP 在途：佇列列由直送領著（<c>sending</c>），寄完由直送記結果再交出。</summary>
    InFlight,
}

public readonly record struct DirectReservation(DirectHandoff Kind, DateTime? SentUtc = null);

/// <summary>
/// 資料庫寫不進時的直送佇列（維運設計 §17.2.6、O2-T8；singleton，只在本實例記憶體）：健康檢查結果寫不進資料庫時，
/// 開啟與升級的轉換改由看門狗的寄送迴圈直接寄（不經 <c>ops.monitor</c>：資料庫不可寫時排程器搶不到租約）。
/// 同一個 <c>TransitionId</c> 只寄一次；重試 1、5、15、60 分鐘、最多 5 次；每小時上限以本實例記憶體計數，超過的不寄、只記 Warning。
/// 通道設定與寄信政策讀不到時用最後一次成功讀到的（密碼仍是加密字串，寄送時才解；信件語系也用那份）；啟動後從沒讀到過就寄不出去；
/// 成功讀到通道停用或設定不完整時，還沒開始寄的信丟掉（同佇列 <c>pending → skipped</c>）。
/// <para>
/// 和補寫的交接以 <c>TransitionId</c> 為單位（§17.13 審查修正）：補寫在交易內呼叫 <see cref="Reserve"/>，還沒開始寄的保留下來、不再開始寄；
/// SMTP 在途的，佇列列寫成由直送領著的 <c>sending</c>，寄完由直送記成 <c>sentDirect</c>（失敗交回 <c>pending</c>）。提交後 <see cref="Confirm"/>；
/// 提交結果不明時保留不動，等去重核對或過了 <see cref="HandoffGrace"/> 查資料庫：有結果列就交出，沒有就收回照寄。
/// 開始寄前也先查一次結果列是否已在資料庫（提交成功但回傳失敗），在的就不寄。寄 SMTP 時不持任何資料庫交易。
/// </para>
/// </summary>
public sealed class DirectAlertQueue(
    IServiceScopeFactory scopes, IAlertMailSender sender, IAlertSecretProtector protector, IOpsEnvironment env, AlertMailOptions options,
    TimeProvider time, ILogger<DirectAlertQueue> logger)
{
    /// <summary>直送佇列上限（技術限制，§17.9）。</summary>
    public const int MaxItems = 20;
    /// <summary>已直送識別碼的保留筆數：與待補寫佇列同一個上限（補寫只會對照得到還在待補寫佇列裡的轉換）。</summary>
    public const int MaxSentRecords = HealthCheckEngine.MaxPending;
    /// <summary>補寫交接後多久還沒確認提交，就查資料庫決定收回或交出（技術限制）。</summary>
    public static readonly TimeSpan HandoffGrace = AlertLimits.ClaimTimeout;
    /// <summary>直送領著佇列列的期限：直送當掉時佇列多久後可以接手（技術限制；涵蓋寬限、一輪寄送與迴圈間隔）。</summary>
    public static readonly TimeSpan HandoffClaim = TimeSpan.FromMinutes(15);

    /// <summary>直送領著佇列列時的領取者。</summary>
    public static string ClaimerFor(string transitionId) => "direct:" + transitionId;

    private sealed class Item(string transitionId, string code, AlertMail mail, DateTime nextAttemptUtc)
    {
        public string TransitionId { get; } = transitionId;
        public string Code { get; } = code;
        public AlertMail Mail { get; } = mail;
        public int Attempts { get; set; }
        public DateTime NextAttemptUtc { get; set; } = nextAttemptUtc;
        /// <summary>SMTP 在途。</summary>
        public bool Sending { get; set; }
        /// <summary>寄送中途回到正常：寄完就丟，失敗不重試。</summary>
        public bool Withdrawn { get; set; }
        /// <summary>補寫交接中（不再開始寄）。</summary>
        public DateTime? ReservedUtc { get; set; }
        /// <summary>交接的提交已確認（或已核對到結果列）。</summary>
        public bool Confirmed { get; set; }
        /// <summary>佇列列由直送領著（在途交接），寄完要記結果交出。</summary>
        public bool RowClaimed { get; set; }
        /// <summary>沒寄出時佇列列交回的狀態（<c>pending</c>，或寫入時已判 <c>skipped</c>）。</summary>
        public AlertOutboxStatus Fallback { get; set; }
        /// <summary>交接中寄出的時間。</summary>
        public DateTime? SentUtc { get; set; }
    }

    private readonly object _sync = new();
    private readonly List<Item> _items = [];
    private readonly Dictionary<string, DateTime> _sent = new(StringComparer.Ordinal);
    private readonly Queue<string> _sentOrder = new();
    private readonly Queue<DateTime> _sentTimes = new();
    private readonly SemaphoreSlim _sending = new(1, 1);
    private SmtpSettings? _settings;
    private AlertEmailPolicy _policy = AlertEmailPolicy.Factory;

    /// <summary>還由直送負責、還沒寄出的信（依順序，給監控與測試；交接中的不算）。</summary>
    public IReadOnlyList<string> PendingTransitionIds()
    {
        lock (_sync) return [.. _items.Where(i => i.ReservedUtc is null).Select(i => i.TransitionId)];
    }

    /// <summary>直送已寄出的時間；沒直送過回 null。</summary>
    public DateTime? SentUtc(string transitionId)
    {
        lock (_sync) return _sent.TryGetValue(transitionId, out var at) ? at : null;
    }

    /// <summary>
    /// 補寫（交易內）或去重核對要把這個轉換交給佇列：還沒開始寄的保留、不再開始寄；在途的記下「佇列列由直送領著」。
    /// <paramref name="fallback"/> 是沒寄出時佇列列該記的狀態：補寫每次都帶這一次的判定，覆寫先前（可能已回滾的）保留；
    /// 去重核對帶 null，保留既有交接的狀態（那是已提交那次補寫的判定），新保留才記 <c>pending</c>（只有 <c>pending</c> 的列會被改由直送領著）。
    /// 之後提交成功呼叫 <see cref="Confirm"/>。
    /// </summary>
    public DirectReservation Reserve(string transitionId, AlertOutboxStatus? fallback)
    {
        lock (_sync)
        {
            var item = _items.Find(i => i.TransitionId == transitionId);
            if (item is null)
                return _sent.TryGetValue(transitionId, out var at) ? new(DirectHandoff.Sent, at) : new(DirectHandoff.None);
            if (item.ReservedUtc is null)
            {
                item.ReservedUtc = time.GetUtcNow().UtcDateTime;
                item.Fallback = fallback ?? AlertOutboxStatus.Pending;
            }
            else if (fallback is { } latest) item.Fallback = latest;
            if (item.SentUtc is { } sent) return new(DirectHandoff.Sent, sent);
            if (!item.Sending) return new(DirectHandoff.Queued);
            item.RowClaimed = true;
            return new(DirectHandoff.InFlight);
        }
    }

    /// <summary>交接的交易已提交：還沒開始寄的直接交出；在途或已寄的，由寄送迴圈記好佇列列再交出。</summary>
    public void Confirm(string transitionId)
    {
        lock (_sync)
        {
            if (_items.Find(i => i.TransitionId == transitionId) is not { ReservedUtc: not null } item) return;
            item.Confirmed = true;
            if (!item.Sending && !item.RowClaimed) _items.Remove(item);
        }
    }

    /// <summary>
    /// 一筆寫不進資料庫的健康檢查結果：比本實例上一筆變糟（新開或升級）且在寄信政策內就排進直送；回到正常就撤掉同一檢查還沒寄出的信
    /// （在途的寄完就丟、失敗不重試；交接中的歸佇列處理）。
    /// </summary>
    public void Consider(HealthObservation o, HealthStatus? previous)
    {
        lock (_sync)
        {
            if (HealthCheckCatalog.Rank(o.Status) == 0)
            {
                if (o.Status != HealthStatus.Ok) return;
                foreach (var i in _items.Where(i => i.Code == o.Code && i.ReservedUtc is null).ToList())
                {
                    if (i.Sending) i.Withdrawn = true;
                    else _items.Remove(i);
                }
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
            if (_items.Count >= MaxItems && _items.FindIndex(i => i.ReservedUtc is null && !i.Sending) is var oldest and >= 0)
            {
                logger.LogWarning("直送佇列已滿（{Max} 封），丟掉最舊的一封：{TransitionId}", MaxItems, _items[oldest].TransitionId);
                _items.RemoveAt(oldest);
            }
            _items.Add(new Item(o.TransitionId, o.Code, mail, time.GetUtcNow().UtcDateTime));
        }
    }

    /// <summary>
    /// 看門狗寄送迴圈的一輪：先重讀通道設定與政策（讀不到沿用上一次），處理交接，再寄到期的信；寄送在交易外、單封逾時 30 秒。
    /// </summary>
    public async Task SendDueAsync(CancellationToken ct)
    {
        await RefreshAsync(ct);
        await _sending.WaitAsync(ct);
        try
        {
            await HandOffAsync(ct);
            var now = time.GetUtcNow().UtcDateTime;
            List<Item> due;
            lock (_sync) due = [.. _items.Where(i => i.ReservedUtc is null && !i.Sending && i.NextAttemptUtc <= now)];
            if (due.Count == 0) return;
            await DropPersistedAsync(due, ct);
            foreach (var item in due)
            {
                if (ct.IsCancellationRequested) return;
                await SendOneAsync(item, ct);
            }
            await HandOffAsync(ct);
        }
        finally
        {
            _sending.Release();
        }
    }

    /// <summary>提交成功但回傳失敗（§17.13 審查修正必修 2）：結果列其實已在資料庫的，事件與佇列列也在，直送不寄、交給佇列。讀不到就照寄。</summary>
    private async Task DropPersistedAsync(List<Item> due, CancellationToken ct)
    {
        List<string> persisted;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            persisted = await scope.ServiceProvider.GetRequiredService<IHealthCheckRepository>()
                .ExistingResultIdsAsync([.. due.Select(i => i.TransitionId)], ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "直送寄前查不到結果是否已寫入，照寄");
            return;
        }
        if (persisted.Count == 0) return;
        lock (_sync)
            foreach (var item in due.Where(i => persisted.Contains(i.TransitionId, StringComparer.Ordinal) && i.ReservedUtc is null && !i.Sending))
            {
                logger.LogInformation("直送信不寄：結果其實已寫入資料庫，交給佇列（{TransitionId}）", item.TransitionId);
                _items.Remove(item);
            }
    }

    /// <summary>
    /// 交接中、不在途的信：已確認的把直送領著的佇列列記好結果交出；過了寬限還沒確認的查結果列，有就交出、沒有（交易沒提交）就收回照寄。
    /// 資料庫寫不進就留到下一輪。
    /// </summary>
    private async Task HandOffAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        List<Item> work;
        lock (_sync) work = [.. _items.Where(i => i.ReservedUtc is { } at && !i.Sending && (i.Confirmed || now - at >= HandoffGrace))];
        foreach (var item in work)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (!item.Confirmed && !await scope.ServiceProvider.GetRequiredService<IHealthCheckRepository>().ResultExistsAsync(item.TransitionId, ct))
                {
                    lock (_sync)
                    {
                        logger.LogInformation("直送交接沒有提交，收回照寄（{TransitionId}）", item.TransitionId);
                        item.ReservedUtc = null;
                        item.RowClaimed = false;
                        if (item.SentUtc is not null) _items.Remove(item);
                    }
                    continue;
                }
                if (item.RowClaimed)
                {
                    var released = await scope.ServiceProvider.GetRequiredService<MediatR.ISender>()
                        .Send(new ReleaseDirectAlertRequest(item.TransitionId, item.SentUtc, item.Fallback), ct);
                    if (!released.IsSuccess) throw new InvalidOperationException(released.Message ?? released.Code);
                    if (released.Data is false)
                        logger.LogWarning("直送領著的佇列列已被佇列接手（{TransitionId}；直送{Sent}）", item.TransitionId, item.SentUtc is null ? "沒寄出" : "已寄出，可能重複");
                }
                lock (_sync) _items.Remove(item);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(e, "直送交接寫不進資料庫，下一輪再試（{TransitionId}）", item.TransitionId);
            }
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
        string? password = null;
        if (settings.PasswordProtected is { } protectedText && (password = protector.TryUnprotect(protectedText)) is null)
        {
            logger.LogError("直送信寄不出去：SMTP 密碼解不開（金鑰可能不見了），請重填（{TransitionId}）", item.TransitionId);
            lock (_sync)
                if (item.ReservedUtc is null) _items.Remove(item);
            return;
        }
        var now = time.GetUtcNow().UtcDateTime;
        lock (_sync)
        {
            // 快照之後被撤回、交接或丟掉的不寄（審查修正必修 1）
            if (!_items.Contains(item) || item.ReservedUtc is not null) return;
            while (_sentTimes.TryPeek(out var at) && at < now - AlertLimits.QuotaWindow) _sentTimes.Dequeue();
            if (_sentTimes.Count >= _policy.MaxPerHour)
            {
                logger.LogWarning("直送信超過每小時上限 {Max} 封，不寄：{TransitionId}", _policy.MaxPerHour, item.TransitionId);
                _items.Remove(item);
                return;
            }
            item.Sending = true;
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
        catch
        {
            lock (_sync) item.Sending = false;
            throw;
        }
        lock (_sync)
        {
            item.Sending = false;
            if (result.Ok)
            {
                _sentTimes.Enqueue(now);
                _sent[item.TransitionId] = now;
                _sentOrder.Enqueue(item.TransitionId);
                while (_sentOrder.Count > MaxSentRecords) _sent.Remove(_sentOrder.Dequeue());
                // 交接中寄出的留著，等記好佇列列再交出
                if (item.ReservedUtc is not null) item.SentUtc = now;
                else _items.Remove(item);
                return;
            }
            // 交接中寄失敗：交回佇列重試，直送不再重試
            if (item.ReservedUtc is not null) return;
            if (item.Withdrawn)
            {
                _items.Remove(item);
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
                // 確定讀到通道停用或設定不完整：還沒開始寄的丟掉（同佇列 pending → skipped）；交接中、在途的照交接處理
                if (_settings is null && _items.RemoveAll(i => i.ReservedUtc is null && !i.Sending) is var dropped and > 0)
                    logger.LogInformation("告警通道停用或設定不完整，丟掉 {Count} 封還沒寄的直送信", dropped);
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "直送讀不到告警通道設定，{Fallback}", _settings is null ? "還沒有可用的設定" : "沿用上一次讀到的");
        }
    }

    private static IncidentSeverity SeverityOf(HealthStatus s) => s == HealthStatus.Critical ? IncidentSeverity.Critical : IncidentSeverity.Warning;
}
