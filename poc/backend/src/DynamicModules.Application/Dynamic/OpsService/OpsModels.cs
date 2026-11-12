namespace DynamicModules.Application.Dynamic.OpsService;

public sealed record ScheduledJobInfo(
    string JobKey, DateTime? LastStartedUtc, DateTime? LastFinishedUtc, string? LastStatus,
    int ConsecutiveFailures, DateTime NextDueUtc, string? LeaseOwner);

public sealed record OpsInfoResponse(
    string Version, string Commit, string BuiltAt, string Edition, DateTime StartedAtUtc, string InstanceId,
    string Database, string? LatestMigration, string? IndexBackfillVersion, IReadOnlyList<ScheduledJobInfo> Jobs);

/// <summary>前端回報的程式錯誤（未驗證的原始內容，由 handler 截斷與驗格式）。</summary>
public sealed record ClientErrorReport(
    string Kind, string? Name, string? Message, string? Stack, string? Route, string? ModuleKey,
    string? Version, string? Locale, string? Browser, DateTimeOffset? OccurredAt, string? LastRequestId);

/// <summary>前端錯誤收件的欄位上限。技術限制：訊息／stack 見設計 §12，其餘見 O1 計畫判斷 J13。</summary>
public static class ClientErrorLimits
{
    public const int MessageMax = 500, StackMax = 4000;
    public const int NameMax = 100, ModuleKeyMax = 100, RouteMax = 200, BrowserMax = 200, VersionMax = 50, LocaleMax = 20;
    public static readonly string[] Kinds = ["vue", "window", "promise", "chunk"];
}
