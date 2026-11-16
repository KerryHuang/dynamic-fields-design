using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Approvals;

/// <summary>關卡路徑函式（計畫 a3c J2；設計 §7.3、§7.6、§17.3 第 5、6 步）：手造關卡實例，不碰資料庫。</summary>
public class ApprovalPathTests
{
    private static ApprovalDefinition Def(params string[] codes) =>
        new(ApprovalShapes.Sequence, null, null, null,
            [.. codes.Select(c => new ApprovalStageDef(c, new I18nText(), null, new ApproverSource(ApprovalSources.Users, UserIds: ["u"]), ApprovalModes.Any, [], null, ApprovalOnEmpty.Fallback))]);

    private static ApprovalStageRun Run(int seq, string? code, string status, string? addedBy = null) =>
        new() { Id = seq, Seq = seq, StageCode = code, Status = status, AddedByUserId = addedBy, AddSignUserIds = addedBy is null ? null : ["x"] };

    private const string Ok = ApprovalStageStatus.Approved, Skip = ApprovalStageStatus.Skipped, Ret = ApprovalStageStatus.Returned,
        Act = ApprovalStageStatus.Active, Closed = ApprovalStageStatus.Closed, Queued = ApprovalStageStatus.Queued;

    [Fact]
    public void Previous_LinearPath_IsLatestApproved()
    {
        var runs = new[] { Run(1, "a", Ok), Run(2, "b", Ok), Run(3, "c", Act) };
        ApprovalPath.Previous(runs, runs[2])!.Seq.Should().Be(2);
    }

    [Fact]
    public void Previous_SkipsSkippedStage()
    {
        var runs = new[] { Run(1, "a", Ok), Run(2, "b", Skip), Run(3, "c", Act) };
        ApprovalPath.Previous(runs, runs[2])!.Seq.Should().Be(1);
    }

    [Fact]
    public void Previous_FirstStage_IsNull()
    {
        var runs = new[] { Run(1, "a", Act) };
        ApprovalPath.Previous(runs, runs[0]).Should().BeNull();
        var skippedFirst = new[] { Run(1, "a", Skip), Run(2, "b", Act) };
        ApprovalPath.Previous(skippedFirst, skippedFirst[1]).Should().BeNull();
    }

    [Fact]
    public void Previous_FallbackStage_IsNull()
    {
        var runs = new[] { Run(1, "_fallback", Act) };
        ApprovalPath.Previous(runs, runs[0]).Should().BeNull();
    }

    [Fact]
    public void Previous_AfterAddSign_IsAddSignRun()
    {
        var runs = new[] { Run(1, "s1", Ok), Run(2, null, Ok, "mgr"), Run(3, "s2", Act) };
        ApprovalPath.Previous(runs, runs[2])!.Seq.Should().Be(2);
    }

    [Fact]
    public void Previous_FromRestartedAddSign_IsAnchorStage()
    {
        // 1、加簽 A、2 被退回、加簽 A 重來 → 重來的加簽 A 的上一關是 1，不是舊的加簽 A
        var runs = new[] { Run(1, "s1", Ok), Run(2, null, Ok, "mgr"), Run(3, "s2", Ret), Run(4, null, Act, "mgr") };
        ApprovalPath.Previous(runs, runs[3])!.Seq.Should().Be(1);
    }

    [Fact]
    public void Previous_TwoAddSignsSameStage_SecondPointsToFirst()
    {
        var runs = new[] { Run(1, "s1", Ok), Run(2, null, Ok, "mgr"), Run(3, null, Act, "mgr") };
        ApprovalPath.Previous(runs, runs[2])!.Seq.Should().Be(2);
    }

    [Fact]
    public void Previous_ClosedQueuedRunBetweenReturnAndRestart_DoesNotBreakTruncation()
    {
        // 會簽關卡的加簽排隊中被退回：排隊的關卡實例 closed，夾在被退回與重來之間
        var runs = new[] { Run(1, "s1", Ok), Run(2, "s2", Ret), Run(3, null, Closed, "mgr"), Run(4, "s1", Act) };
        ApprovalPath.Previous(runs, runs[3]).Should().BeNull();
    }

    [Fact]
    public void NextStageIndex_AfterAddSignRestart_IsReturnedStage()
    {
        var def = Def("s1", "s2", "s3");
        var runs = new[] { Run(1, "s1", Ok), Run(2, null, Ok, "mgr"), Run(3, "s2", Ret), Run(4, null, Ok, "mgr") };
        ApprovalPath.NextStageIndex(def, runs).Should().Be(1);
    }

    [Fact]
    public void NextStageIndex_AfterReturnToStage2FromStage3_IsStage3()
    {
        var def = Def("s1", "s2", "s3");
        var runs = new[] { Run(1, "s1", Ok), Run(2, "s2", Ok), Run(3, "s3", Ret), Run(4, "s2", Ok) };
        ApprovalPath.NextStageIndex(def, runs).Should().Be(2);
    }

    [Fact]
    public void NextStageIndex_SkippedThenReturned_ReevaluatesSkipped()
    {
        // 1 同意、2 跳過、3 退回到 1、1 重來同意 → 2 的索引（條件重新求值）
        var def = Def("s1", "s2", "s3");
        var runs = new[] { Run(1, "s1", Ok), Run(2, "s2", Skip), Run(3, "s3", Ret), Run(4, "s1", Ok) };
        ApprovalPath.NextStageIndex(def, runs).Should().Be(1);
    }

    [Fact]
    public void NextStageIndex_NoDefinedStageOnPath_IsZero()
    {
        ApprovalPath.NextStageIndex(Def("s1"), []).Should().Be(0);
        ApprovalPath.NextStageIndex(Def("s1"), [Run(1, null, Ok, "mgr")]).Should().Be(0);
    }

    [Fact]
    public void NextStageIndex_AllModeAddSigns_QueuedRunsAfterStage()
    {
        var def = Def("s1", "s2", "s3");
        var runs = new[] { Run(1, "s1", Ok), Run(2, "s2", Ok), Run(3, null, Ok, "a"), Run(4, null, Ok, "b") };
        ApprovalPath.NextStageIndex(def, runs).Should().Be(2);
        ApprovalPath.Effective(runs).Select(r => r.Seq).Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public void Effective_IgnoresActiveQueuedAndClosed()
    {
        var runs = new[] { Run(1, "s1", Ok), Run(2, "s2", Act), Run(3, null, Queued, "a"), Run(4, null, Closed, "b") };
        ApprovalPath.Effective(runs).Select(r => r.Seq).Should().Equal(1);
    }

    [Fact]
    public void Effective_ReturnedAddSign_RequeuedAfterTarget()
    {
        // a3c 審查 M1：s2 會簽兩人各自加簽 A、B；B 退回上一關 → A 重來（A'），B 排回成 queued 複本（B'），Seq 在重來之後
        var def = Def("s1", "s2", "s3");
        var waiting = new[] { Run(1, "s1", Ok), Run(2, "s2", Ok), Run(3, null, Ok, "fin1"), Run(4, null, Ret, "fin2"), Run(5, null, Act, "fin1"), Run(6, null, Queued, "fin2") };
        ApprovalPath.Previous(waiting, waiting[4])!.Seq.Should().Be(2);   // A' 的上一關是 s2
        ApprovalPath.Effective(waiting).Select(r => r.Seq).Should().Equal(1, 2);

        var requeued = new[] { Run(1, "s1", Ok), Run(2, "s2", Ok), Run(3, null, Ok, "fin1"), Run(4, null, Ret, "fin2"), Run(5, null, Ok, "fin1"), Run(6, null, Act, "fin2") };
        ApprovalPath.Previous(requeued, requeued[5])!.Seq.Should().Be(5);   // 排回的 B' 上一關是 A'
        requeued[5].Status = Ok;
        ApprovalPath.Effective(requeued).Select(r => r.Seq).Should().Equal(1, 2, 5, 6);
        ApprovalPath.NextStageIndex(def, requeued).Should().Be(2);   // B' 簽完才到 s3
    }
}
