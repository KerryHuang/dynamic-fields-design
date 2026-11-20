using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.PolicyService;

namespace DynamicModules.WebAPI.Time;

/// <summary>營運時區（政策 locale.timeZone，固定 UTC 偏移）的牆上時間；偏移只從全系統政策快照取。Kind 一律 Unspecified（PostgreSQL 的 timestamp 欄位不收 Utc）。</summary>
public sealed class ZoneBusinessTime(CachedSystemPolicy cache) : IBusinessTime
{
    public DateTime Now => DateTime.SpecifyKind(DateTime.UtcNow + cache.BusinessOffset, DateTimeKind.Unspecified);
    public DateOnly Today => DateOnly.FromDateTime(Now);
}
