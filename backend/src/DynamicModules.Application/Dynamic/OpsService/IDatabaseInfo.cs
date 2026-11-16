namespace DynamicModules.Application.Dynamic.OpsService;

public interface IDatabaseInfo
{
    /// <summary>小寫的資料庫種類，如 <c>sqlite</c>、<c>postgresql</c>。</summary>
    string Provider { get; }

    /// <summary>已套用的最新 migration 識別（含時間戳）；沒有則 null。</summary>
    Task<string?> LatestMigrationAsync(CancellationToken ct);
}
