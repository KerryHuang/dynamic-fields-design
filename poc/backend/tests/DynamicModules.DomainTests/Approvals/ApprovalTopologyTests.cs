using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Approvals;

/// <summary>簽核鎖定狀態與可編輯狀態（簽核設計 §9.3）。</summary>
public class ApprovalTopologyTests
{
    private const long Draft = 1, Confirmed = 2, Partial = 3, Received = 4, Returned = 5, Cancelled = 6;

    private static WorkflowTransition Plain(long from, long to) => new(from, to, []);
    private static WorkflowTransition Approval(long from, long to) => new(from, to, [], new TransitionApproval(true));

    /// <summary>1 →(需簽核) 2 → 3 → 4；2 → 5（可編輯）；5 →(需簽核) 2。</summary>
    private static Workflow Base(params WorkflowTransition[] extra) => new()
    {
        InitialOptionId = Draft,
        States =
        [
            new WorkflowState(Draft), new WorkflowState(Confirmed), new WorkflowState(Partial), new WorkflowState(Received),
            new WorkflowState(Returned, Editable: true), new WorkflowState(Cancelled)
        ],
        Transitions =
        [
            Approval(Draft, Confirmed), Plain(Confirmed, Partial), Plain(Partial, Received), Plain(Confirmed, Returned),
            Approval(Returned, Confirmed), .. extra
        ]
    };

    [Fact]
    public void LockedStates_TargetAndDownstream_StopAtEditable()
    {
        ApprovalTopology.LockedStates(Base()).Should().BeEquivalentTo([Confirmed, Partial, Received]);
    }

    [Fact]
    public void LockedStates_ApprovalOutOfLockedKeepsLocked()
    {
        var wf = Base(Approval(Partial, Cancelled));

        ApprovalTopology.LockedStates(wf).Should().BeEquivalentTo([Confirmed, Partial, Received, Cancelled]);
        ApprovalTopology.IsLocked(wf, Partial).Should().BeTrue();
        ApprovalTopology.IsLocked(wf, Draft).Should().BeFalse();
        ApprovalTopology.IsLocked(wf, null).Should().BeFalse();
    }

    [Fact]
    public void EditableStates_IncludeInitial()
    {
        ApprovalTopology.EditableStates(Base()).Should().BeEquivalentTo([Draft, Returned]);
    }

    [Fact]
    public void LockedStates_NoApproval_Empty()
    {
        var wf = new Workflow
        {
            InitialOptionId = Draft, States = [new WorkflowState(Draft), new WorkflowState(Confirmed, true)],
            Transitions = [Plain(Draft, Confirmed)]
        };
        ApprovalTopology.LockedStates(wf).Should().BeEmpty();
    }

    [Fact]
    public void LockedStates_ApprovalTargetMarkedEditable_StillLocked()
    {
        var wf = new Workflow
        {
            InitialOptionId = Draft, States = [new WorkflowState(Draft), new WorkflowState(Confirmed, Editable: true)],
            Transitions = [Approval(Draft, Confirmed)]
        };
        ApprovalTopology.LockedStates(wf).Should().BeEquivalentTo([Confirmed]);
    }
}
