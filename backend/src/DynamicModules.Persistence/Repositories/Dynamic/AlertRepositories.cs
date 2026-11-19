using System.Linq.Expressions;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class AlertChannelRepository(DynamicDbContext db) : IAlertChannelRepository
{
    private DbSet<AlertChannel> Channels => db.Set<AlertChannel>();

    public Task<AlertChannel?> GetAsync(string kind, CancellationToken ct) => Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Kind == kind, ct);

    public Task<AlertChannel?> GetTrackedAsync(string kind, CancellationToken ct) => Channels.FirstOrDefaultAsync(c => c.Kind == kind, ct);

    public void Add(AlertChannel channel) => Channels.Add(channel);

    public async Task<bool> LockAsync(string kind, DateTime nowUtc, CancellationToken ct) =>
        await Channels.Where(c => c.Kind == kind).ExecuteUpdateAsync(s => s.SetProperty(c => c.LastClaimUtc, nowUtc), ct) == 1;

    public Task RecordSuccessAsync(string kind, DateTime nowUtc, CancellationToken ct) =>
        Channels.Where(c => c.Kind == kind).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.LastSuccessUtc, nowUtc)
            .SetProperty(c => c.ConsecutiveFailures, 0), ct);

    public Task RecordFailureAsync(string kind, DateTime nowUtc, string error, CancellationToken ct) =>
        Channels.Where(c => c.Kind == kind).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.LastFailureUtc, nowUtc)
            .SetProperty(c => c.LastError, error)
            .SetProperty(c => c.ConsecutiveFailures, c => c.ConsecutiveFailures + 1), ct);
}

public class AlertOutboxRepository(DynamicDbContext db) : IAlertOutboxRepository
{
    private DbSet<AlertOutbox> Rows => db.Set<AlertOutbox>();

    public void Add(AlertOutbox row) => Rows.Add(row);

    public async Task<int> CountQuotaUsedAsync(string channel, DateTime sinceUtc, CancellationToken ct) =>
        await Rows.CountAsync(r => r.Channel == channel && r.Transition != AlertTransition.Digest
            && ((r.Status == AlertOutboxStatus.Sent && r.SentUtc >= sinceUtc) || r.Status == AlertOutboxStatus.Sending), ct);

    public Task<AlertOutbox?> NextClaimableAsync(string channel, DateTime nowUtc, bool digestOnly, CancellationToken ct) =>
        Rows.AsNoTracking()
            .Where(r => r.Channel == channel
                && ((r.Status == AlertOutboxStatus.Pending && r.NextAttemptUtc <= nowUtc && (!digestOnly || r.Transition == AlertTransition.Digest))
                    || (r.Status == AlertOutboxStatus.Sending && r.ClaimedUntilUtc < nowUtc)))
            .OrderBy(r => r.Id).FirstOrDefaultAsync(ct);

    public async Task<bool> TryClaimAsync(long id, string claimedBy, DateTime nowUtc, DateTime claimedUntilUtc, CancellationToken ct) =>
        await Rows.Where(r => r.Id == id
                && ((r.Status == AlertOutboxStatus.Pending && r.NextAttemptUtc <= nowUtc)
                    || (r.Status == AlertOutboxStatus.Sending && r.ClaimedUntilUtc < nowUtc)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, AlertOutboxStatus.Sending)
                .SetProperty(r => r.ClaimedBy, claimedBy)
                .SetProperty(r => r.ClaimedUntilUtc, claimedUntilUtc)
                .SetProperty(r => r.Attempts, r => r.Attempts + 1), ct) == 1;

    public Task<int> DigestPendingAsync(string channel, DateTime nowUtc, CancellationToken ct) =>
        Rows.Where(r => r.Channel == channel && r.Status == AlertOutboxStatus.Pending && r.ClaimedBy == null && r.NextAttemptUtc <= nowUtc
                && r.Transition != AlertTransition.Digest)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, AlertOutboxStatus.Digested)
                .SetProperty(r => r.NextAttemptUtc, nowUtc), ct);

    public Task<bool> DigestExistsSinceAsync(string channel, DateTime sinceUtc, CancellationToken ct) =>
        Rows.AnyAsync(r => r.Channel == channel && r.Transition == AlertTransition.Digest && r.CreatedUtc >= sinceUtc, ct);

    public Task<List<AlertOutbox>> ListUnattachedDigestedAsync(string channel, DateTime beforeUtc, CancellationToken ct) =>
        Rows.AsNoTracking()
            .Where(r => r.Channel == channel && r.Status == AlertOutboxStatus.Digested && r.DigestOutboxId == null && r.NextAttemptUtc < beforeUtc)
            .OrderBy(r => r.Id).ToListAsync(ct);

    public Task<int> AttachToDigestAsync(IReadOnlyCollection<long> ids, long digestId, CancellationToken ct) =>
        Rows.Where(r => ids.Contains(r.Id) && r.Status == AlertOutboxStatus.Digested && r.DigestOutboxId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.DigestOutboxId, digestId), ct);

    public async Task<bool> TryCompleteAsync(long id, string claimedBy, AlertOutboxStatus status, DateTime nextAttemptUtc, string? error,
        DateTime? sentUtc, CancellationToken ct) =>
        await Rows.Where(r => r.Id == id && r.ClaimedBy == claimedBy && r.Status == AlertOutboxStatus.Sending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, status)
                .SetProperty(r => r.NextAttemptUtc, nextAttemptUtc)
                .SetProperty(r => r.LastError, error)
                .SetProperty(r => r.SentUtc, sentUtc)
                .SetProperty(r => r.ClaimedBy, (string?)null)
                .SetProperty(r => r.ClaimedUntilUtc, (DateTime?)null), ct) == 1;

    public async Task<bool> MarkSentDirectAsync(string transitionId, DateTime sentUtc, CancellationToken ct) =>
        await Rows.Where(r => r.TransitionId == transitionId && r.Status == AlertOutboxStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, AlertOutboxStatus.SentDirect)
                .SetProperty(r => r.SentUtc, sentUtc), ct) > 0;

    public async Task<bool> ClaimForDirectAsync(string transitionId, string claimedBy, DateTime claimedUntilUtc, CancellationToken ct) =>
        await Rows.Where(r => r.TransitionId == transitionId && r.Status == AlertOutboxStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, AlertOutboxStatus.Sending)
                .SetProperty(r => r.ClaimedBy, claimedBy)
                .SetProperty(r => r.ClaimedUntilUtc, claimedUntilUtc), ct) > 0;

    public async Task<bool> ReleaseDirectAsync(string transitionId, string claimedBy, AlertOutboxStatus status, DateTime? sentUtc, DateTime nowUtc,
        CancellationToken ct) =>
        await Rows.Where(r => r.TransitionId == transitionId && r.Status == AlertOutboxStatus.Sending && r.ClaimedBy == claimedBy)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, status)
                .SetProperty(r => r.SentUtc, sentUtc)
                .SetProperty(r => r.NextAttemptUtc, nowUtc)
                .SetProperty(r => r.ClaimedBy, (string?)null)
                .SetProperty(r => r.ClaimedUntilUtc, (DateTime?)null), ct) > 0;

    public Task<int> SkipPendingAsync(string channel, CancellationToken ct) =>
        Rows.Where(r => r.Channel == channel && r.Status == AlertOutboxStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, AlertOutboxStatus.Skipped), ct);

    public async Task<int> PurgeBatchAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct)
    {
        var purgeable = Purgeable(cutoffUtc);
        var upper = await Rows.Where(purgeable).OrderBy(r => r.Id).Take(batchSize).Select(r => (long?)r.Id).MaxAsync(ct);
        if (upper is not { } max) return 0;
        return await Rows.Where(purgeable).Where(r => r.Id <= max).ExecuteDeleteAsync(ct);
    }

    /// <summary>已寄完；<c>digested</c> 要等它的摘要信不在 pending／sending（摘要信已清掉也算寄完）。</summary>
    private Expression<Func<AlertOutbox, bool>> Purgeable(DateTime cutoffUtc) => r => r.CreatedUtc < cutoffUtc
        && (r.Status == AlertOutboxStatus.Sent || r.Status == AlertOutboxStatus.SentDirect || r.Status == AlertOutboxStatus.Failed
            || r.Status == AlertOutboxStatus.Skipped
            || (r.Status == AlertOutboxStatus.Digested && r.DigestOutboxId != null
                && !Rows.Any(d => d.Id == r.DigestOutboxId && (d.Status == AlertOutboxStatus.Pending || d.Status == AlertOutboxStatus.Sending))));
}
