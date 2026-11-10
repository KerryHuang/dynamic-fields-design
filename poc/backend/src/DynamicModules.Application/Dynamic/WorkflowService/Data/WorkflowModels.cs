using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.WorkflowService.Data;

public record WorkflowInput(long StatusFieldId, long InitialOptionId, List<WorkflowState> States, List<WorkflowTransition> Transitions);

/// <summary>LockedStates／EditableStates／LockedFieldIds：簽核鎖定狀態、可編輯狀態、鎖定欄位（簽核設計 §9.3），給設計器標示。</summary>
public record WorkflowResponse(
    long Id, long ModuleId, long StatusFieldId, long InitialOptionId, List<WorkflowState> States, List<WorkflowTransition> Transitions,
    IReadOnlyList<long> LockedStates, IReadOnlyList<long> EditableStates, IReadOnlyList<long> LockedFieldIds);

public static class WorkflowMapper
{
    public static WorkflowResponse ToResponse(this Workflow w, IEnumerable<long> lockedFieldIds) =>
        new(w.Id, w.ModuleId, w.StatusFieldId, w.InitialOptionId, [.. w.States], [.. w.Transitions],
            [.. ApprovalTopology.LockedStates(w).Order()], [.. ApprovalTopology.EditableStates(w).Order()], [.. lockedFieldIds.Order()]);
}
