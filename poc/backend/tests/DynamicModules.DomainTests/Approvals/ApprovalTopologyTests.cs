using System.Text.Json;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
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

    // ───────── Lock：鎖定狀態、鎖定欄位（含附件）與明細鎖（設計 §9.3）─────────

    private const long TotalF = 11, LineSumF = 12, DiscountF = 13, StatusF = 14, FilesF = 15, SupplierF = 16;

    private static Field F(long id, FieldType type, FieldConfig? config = null) =>
        new() { Id = id, Key = $"f_{id}", Type = type, Status = FieldStatus.Active, Config = config ?? new FieldConfig() };

    private static Module Doc() => new()
    {
        Id = 1, Key = "doc",
        Blocks =
        [
            new Block
            {
                Fields =
                [
                    F(TotalF, FieldType.Formula, new FieldConfig { Formula = $"{{{LineSumF}}} - {{{DiscountF}}}" }),
                    F(LineSumF, FieldType.Rollup, new FieldConfig { Rollup = new RollupConfig(900, RollupFunction.Sum, 901, null) }),
                    F(DiscountF, FieldType.Decimal), F(StatusF, FieldType.Enum), F(FilesF, FieldType.Attachment), F(SupplierF, FieldType.Text)
                ]
            }
        ]
    };

    private static Workflow Flow() => new()
    {
        StatusFieldId = StatusF, InitialOptionId = Draft, States = [new WorkflowState(Draft), new WorkflowState(Confirmed)],
        Transitions = [Approval(Draft, Confirmed)]
    };

    private static ApprovalDefinition DefOn(long field) => new(ApprovalShapes.Sequence,
        JsonDocument.Parse($$"""{"all":[{"field":{{field}},"op":"gte","value":1}]}""").RootElement.Clone(), null, null, []);

    [Fact]
    public void Lock_IncludesAttachmentFields()
    {
        var l = ApprovalTopology.Lock(Doc(), Flow(), [DefOn(SupplierF)], _ => true);

        l.FieldIds.Should().BeEquivalentTo([SupplierF, FilesF]);
    }

    [Fact]
    public void Lock_OwnDetailRollupInClosure_DetailRowsLocked()
    {
        var l = ApprovalTopology.Lock(Doc(), Flow(), [DefOn(TotalF)], _ => true);

        l.DetailRowsLocked.Should().BeTrue();
        l.LockedStateIds.Should().BeEquivalentTo([Confirmed]);
    }

    [Fact]
    public void Lock_OtherModuleRollupInClosure_DetailRowsNotLocked()
    {
        var l = ApprovalTopology.Lock(Doc(), Flow(), [DefOn(TotalF)], _ => false);

        l.DetailRowsLocked.Should().BeFalse();
        l.FieldIds.Should().Contain(LineSumF);
    }

    [Fact]
    public void Lock_StatusFieldExcluded()
    {
        var l = ApprovalTopology.Lock(Doc(), Flow(), [DefOn(StatusF)], _ => true);

        l.FieldIds.Should().NotContain(StatusF);
    }

    [Fact]
    public void LockedFields_EqualsLockFieldIds()
    {
        var defs = new[] { DefOn(TotalF) };

        ApprovalTopology.LockedFields(Doc(), Flow(), defs).Should().BeEquivalentTo(ApprovalTopology.Lock(Doc(), Flow(), defs, _ => true).FieldIds);
    }

    [Fact]
    public void IsLockedState_NullAndNone()
    {
        ApprovalLock.None.IsLockedState(Confirmed).Should().BeFalse();
        ApprovalTopology.Lock(Doc(), Flow(), [DefOn(TotalF)], _ => true).IsLockedState(null).Should().BeFalse();
    }
}
