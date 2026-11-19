using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

/// <summary>
/// <see cref="IRuleHealthRepository"/>：兩種資料庫同一份 LINQ。SET 右邊讀的都是更新前的值（SQLite、PostgreSQL 皆然），
/// 所以「取較晚者」與條件運算式裡的 <c>LastFailureUtc</c>、<c>StreakResetUtc</c> 是舊值。
/// </summary>
public class RuleHealthRepository(DynamicDbContext db) : IRuleHealthRepository
{
    private IQueryable<RuleHealth> Rows => db.Set<RuleHealth>();
    private IQueryable<RuleFailure> Failures => db.Set<RuleFailure>();

    private IQueryable<RuleHealth> One(string kind, long id) => Rows.Where(h => h.RuleKind == kind && h.RuleId == id);

    public Task<List<RuleHealth>> ListAsync(string ruleKind, IReadOnlyCollection<long> ruleIds, CancellationToken ct) =>
        ruleIds.Count == 0
            ? Task.FromResult(new List<RuleHealth>())
            : Rows.AsNoTracking().Where(h => h.RuleKind == ruleKind && ruleIds.Contains(h.RuleId)).ToListAsync(ct);

    public Task<RuleHealth?> GetAsync(string ruleKind, long ruleId, CancellationToken ct) => One(ruleKind, ruleId).AsNoTracking().FirstOrDefaultAsync(ct);

    public Task<int> BumpFailureAsync(string ruleKind, long ruleId, DateTime failedUtc, string errorCode, string? requestId, CancellationToken ct) =>
        One(ruleKind, ruleId).ExecuteUpdateAsync(s => s
            .SetProperty(h => h.ConsecutiveFailures, h => h.ConsecutiveFailures + (h.StreakResetUtc == null || failedUtc > h.StreakResetUtc ? 1 : 0))
            .SetProperty(h => h.LastErrorCode, h => h.LastFailureUtc == null || h.LastFailureUtc <= failedUtc ? errorCode : h.LastErrorCode)
            .SetProperty(h => h.LastRequestId, h => h.LastFailureUtc == null || h.LastFailureUtc <= failedUtc ? requestId : h.LastRequestId)
            .SetProperty(h => h.LastFailureUtc, h => h.LastFailureUtc == null || h.LastFailureUtc < failedUtc ? failedUtc : h.LastFailureUtc), ct);

    public void Add(RuleHealth row) => db.Set<RuleHealth>().Add(row);

    public void Detach(RuleHealth row) => db.Entry(row).State = EntityState.Detached;

    /// <summary>
    /// 兩句（§17.14 審查修正 1）：第一句推 <c>StreakResetUtc</c>（取得列鎖，與補記第一句同一把）；第二句另起一句重數計數＝晚於評估時間、已記下的失敗列數。
    /// 分兩句是因為 PostgreSQL 的 UPDATE 等到列鎖之後只重讀目標列，同一句裡的子查詢仍用等鎖之前的快照，會漏數剛提交的那筆補記的失敗列；
    /// 第二句在 Read Committed 下取新快照，看得到所有在這把鎖之前提交的補記，之後的補記要等這個交易結束才會加一。
    /// 晚於評估時間的失敗都有失敗列（新增條件是晚於當時的 <c>StreakResetUtc</c>，而它早於這次評估時間）；只有成功晚了整個窗口才寫 reset 時，
    /// 窗口外的那幾筆已被清掉而少數（只影響畫面上的連續失敗數，暫停只看窗口內列數）。
    /// </summary>
    public async Task<int> ResetStreakAsync(string ruleKind, long ruleId, DateTime evaluatedUtc, CancellationToken ct)
    {
        var moved = await One(ruleKind, ruleId).Where(h => h.StreakResetUtc == null || h.StreakResetUtc < evaluatedUtc)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.StreakResetUtc, evaluatedUtc), ct);
        if (moved == 0) return 0;
        await One(ruleKind, ruleId).ExecuteUpdateAsync(s => s.SetProperty(h => h.ConsecutiveFailures,
            h => Failures.Count(f => f.RuleKind == h.RuleKind && f.RuleId == h.RuleId && f.FailedUtc > evaluatedUtc)), ct);
        return moved;
    }

    public void AddFailure(RuleFailure failure) => db.Set<RuleFailure>().Add(failure);

    public Task PruneFailuresAsync(string ruleKind, long ruleId, DateTime windowStartUtc, DateTime? streakResetUtc, CancellationToken ct) =>
        Failures.Where(f => f.RuleKind == ruleKind && f.RuleId == ruleId
                && (f.FailedUtc < windowStartUtc || (streakResetUtc != null && f.FailedUtc <= streakResetUtc)))
            .ExecuteDeleteAsync(ct);

    public Task<int> CountFailuresAsync(string ruleKind, long ruleId, DateTime fromUtc, DateTime toUtc, DateTime? streakResetUtc, CancellationToken ct) =>
        Failures.Where(f => f.RuleKind == ruleKind && f.RuleId == ruleId && f.FailedUtc >= fromUtc && f.FailedUtc <= toUtc
                && (streakResetUtc == null || f.FailedUtc > streakResetUtc))
            .CountAsync(ct);

    public async Task<bool> TryPauseAsync(string ruleKind, long ruleId, DateTime pausedUtc, CancellationToken ct) =>
        await One(ruleKind, ruleId).Where(h => h.PausedUtc == null).ExecuteUpdateAsync(s => s
            .SetProperty(h => h.PausedUtc, pausedUtc)
            .SetProperty(h => h.LastPausedUtc, pausedUtc)
            .SetProperty(h => h.PauseCount, h => h.PauseCount + 1)
            .SetProperty(h => h.SkippedWhilePaused, 0), ct) == 1;

    public Task AddSkippedAsync(string ruleKind, long ruleId, int count, CancellationToken ct) =>
        One(ruleKind, ruleId).Where(h => h.PausedUtc != null)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.SkippedWhilePaused, h => h.SkippedWhilePaused + count), ct);

    public async Task<bool> TryResumeAsync(string ruleKind, long ruleId, DateTime resumedUtc, string resumedBy, CancellationToken ct)
    {
        var resumed = await One(ruleKind, ruleId).Where(h => h.PausedUtc != null).ExecuteUpdateAsync(s => s
            .SetProperty(h => h.PausedUtc, (DateTime?)null)
            .SetProperty(h => h.ResumedUtc, resumedUtc)
            .SetProperty(h => h.ResumedBy, resumedBy)
            .SetProperty(h => h.ConsecutiveFailures, 0)
            .SetProperty(h => h.StreakResetUtc, h => h.StreakResetUtc == null || h.StreakResetUtc < resumedUtc ? resumedUtc : h.StreakResetUtc), ct) == 1;
        if (resumed) await Failures.Where(f => f.RuleKind == ruleKind && f.RuleId == ruleId).ExecuteDeleteAsync(ct);
        return resumed;
    }

    public async Task DeleteAsync(string ruleKind, IReadOnlyCollection<long> ruleIds, CancellationToken ct)
    {
        if (ruleIds.Count == 0) return;
        await Failures.Where(f => f.RuleKind == ruleKind && ruleIds.Contains(f.RuleId)).ExecuteDeleteAsync(ct);
        await Rows.Where(h => h.RuleKind == ruleKind && ruleIds.Contains(h.RuleId)).ExecuteDeleteAsync(ct);
    }

    public async Task<int> PurgeFailuresBatchAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct)
    {
        var upper = await Failures.Where(f => f.FailedUtc < cutoffUtc).OrderBy(f => f.Id).Take(batchSize).Select(f => (long?)f.Id).MaxAsync(ct);
        if (upper is not { } max) return 0;
        return await Failures.Where(f => f.FailedUtc < cutoffUtc && f.Id <= max).ExecuteDeleteAsync(ct);
    }
}
