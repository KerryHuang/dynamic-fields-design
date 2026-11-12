namespace DynamicModules.Application.Dynamic.OpsService;

public sealed record ScheduledJobInfo(
    string JobKey, DateTime? LastStartedUtc, DateTime? LastFinishedUtc, string? LastStatus,
    int ConsecutiveFailures, DateTime NextDueUtc, string? LeaseOwner);

public sealed record OpsInfoResponse(
    string Version, string Commit, string BuiltAt, string Edition, DateTime StartedAtUtc, string InstanceId,
    string Database, string? LatestMigration, string? IndexBackfillVersion, IReadOnlyList<ScheduledJobInfo> Jobs);
