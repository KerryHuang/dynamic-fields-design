namespace DynamicModules.Application.Dynamic.DemoService.Data;

/// <summary>State：notGenerated、running、generated、conflict、failed。</summary>
public record DemoDataStatus(
    string State, List<string> Conflicts, string? Message,
    DateTime? StartedAt, DateTime? FinishedAt, int Done, int Total)
{
    public static DemoDataStatus Of(DemoRunSnapshot s, string state) =>
        new(state, [.. s.Conflicts], s.Message, s.StartedAt, s.FinishedAt, s.Done, s.Total);
}
