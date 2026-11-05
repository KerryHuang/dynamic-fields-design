namespace DynamicModules.Application.Dynamic.DemoService;

public enum DemoRunState { Idle, Running, Succeeded, Failed, Conflict }

/// <summary>執行器目前（或最後一次）的執行狀態；只記在這個程序內。</summary>
public sealed record DemoRunSnapshot(
    DemoRunState State, IReadOnlyList<string> Conflicts, string? Message,
    DateTime? StartedAt, DateTime? FinishedAt, int Done, int Total)
{
    public static DemoRunSnapshot Idle { get; } = new(DemoRunState.Idle, [], null, null, null, 0, 0);
}

/// <summary>在背景產生 Demo 資料（singleton），同時只會有一次在跑。</summary>
public interface IDemoDataRunner
{
    DemoRunSnapshot Current { get; }

    /// <summary>開始背景產生；回 false 表示已在執行。startup 只影響衝突時的 log 等級。</summary>
    bool TryStart(bool startup = false);
}
