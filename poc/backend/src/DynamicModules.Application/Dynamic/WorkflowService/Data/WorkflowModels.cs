using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.WorkflowService.Data;

public record WorkflowInput(long StatusFieldId, long InitialOptionId, List<WorkflowState> States, List<WorkflowTransition> Transitions);

public record WorkflowResponse(
    long Id, long ModuleId, long StatusFieldId, long InitialOptionId, List<WorkflowState> States, List<WorkflowTransition> Transitions);

public static class WorkflowMapper
{
    public static WorkflowResponse ToResponse(this Workflow w) =>
        new(w.Id, w.ModuleId, w.StatusFieldId, w.InitialOptionId, [.. w.States], [.. w.Transitions]);
}
