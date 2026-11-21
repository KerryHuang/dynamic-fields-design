using System.Data;
using System.Reflection;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Persistence.Postgres;
using DynamicModules.Persistence.Repositories.Dynamic;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static DynamicModules.ApplicationTests.Dynamic.Approvals.ApprovalGraphTestKit;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>
/// 無鎖模式下兩個簽核請求真的同時搶同一張單的處理權（J16）：兩邊都讀到同一個 RowVersion（屏障保證）之後才各自條件更新，
/// 先到的取得、後到的卡在列鎖上，對方提交後以新值比對得 0 列，重新讀取再取得（重試）。最後兩個動作都成功、版本換了兩次。
/// </summary>
[Trait("Category", "Postgres")]
public class ApprovalClaimInterleavingTests : PgInterleavingTestBase
{
    protected override bool Unserialized => true;

    private readonly ClaimProbe _probe = new();

    protected override void Extra(IServiceCollection services) =>
        services.AddScoped<IApprovalInstanceRepository>(sp => ClaimProbeRepository.Wrap(new ApprovalInstanceRepository(sp.GetRequiredService<DynamicDbContext>()), _probe));

    internal sealed class ClaimProbe
    {
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;
        public int Reads, Bumps;

        /// <summary>讀到 RowVersion 的人在這裡等，直到第二個人也讀到；之後（重試的讀取）不再等。</summary>
        public async Task ArriveAsync()
        {
            Interlocked.Increment(ref Reads);
            if (Interlocked.Increment(ref _arrived) >= 2) _both.TrySetResult();
            await _both.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    internal class ClaimProbeRepository : DispatchProxy
    {
        private IApprovalInstanceRepository _inner = null!;
        private ClaimProbe _probe = null!;

        public static IApprovalInstanceRepository Wrap(IApprovalInstanceRepository inner, ClaimProbe probe)
        {
            var proxy = Create<IApprovalInstanceRepository, ClaimProbeRepository>();
            var self = (ClaimProbeRepository)(object)proxy;
            self._inner = inner;
            self._probe = probe;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            object? result;
            try { result = method!.Invoke(_inner, args); }
            catch (TargetInvocationException e) { throw e.InnerException!; }
            if (method.Name == nameof(IApprovalInstanceRepository.TryBumpRowVersionAsync)) Interlocked.Increment(ref _probe.Bumps);
            return method.Name == nameof(IApprovalInstanceRepository.GetRowVersionAsync) ? After((Task<string?>)result!) : result;
        }

        private async Task<string?> After(Task<string?> read)
        {
            var value = await read;
            await _probe.ArriveAsync();
            return value;
        }
    }

    [PostgresFact]
    public async Task TwoBranchApprovals_RaceForTheClaim_LoserWaitsRetriesAndBothSucceed()
    {
        var r = await Rig(G(
            [Start(), Fork("f"), Approval("a", Users("fin1")), Approval("b", Users("buyer2")), Join("j", "f"), End()],
            [E("e0", "start", "f"), E("fa", "f", "a"), E("fb", "f", "b"), E("aj", "a", "j"), E("bj", "b", "j"), E("je", "j", "end")]));
        var po = await NewPoBy("buyer1", r);
        var submitted = await SubmitBy("buyer1", po.Id, r.Confirmed);
        submitted.IsCreated.Should().BeTrue(Dump(submitted));
        var id = ((SubmitApprovalResponse)submitted.Data!).InstanceId!.Value;
        var before = Inst(id).RowVersion;
        var taskA = TasksOf(id).Single(t => t.AssigneeUserId == "fin1" && t.Status == ApprovalTaskStatus.Open);
        var taskB = TasksOf(id).Single(t => t.AssigneeUserId == "buyer2" && t.Status == ApprovalTaskStatus.Open);
        _probe.Reads = 0;
        _probe.Bumps = 0;

        var a = By("fin1", () => Send(new ApproveTaskRequest(taskA.Id, before, null, null)));
        var b = By("buyer2", () => Send(new ApproveTaskRequest(taskB.Id, before, null, null)));
        var results = await Within(Task.WhenAll(a, b));

        results.Should().OnlyContain(x => x.IsSuccess, string.Join(" | ", results.Select(Dump)));
        TasksOf(id).Single(t => t.Id == taskA.Id).Status.Should().Be(ApprovalTaskStatus.Approved);
        TasksOf(id).Single(t => t.Id == taskB.Id).Status.Should().Be(ApprovalTaskStatus.Approved);
        Inst(id).RowVersion.Should().NotBe(before);
        _probe.Bumps.Should().BeGreaterThanOrEqualTo(3, "兩個第一次嘗試（一成一敗）加上敗者的重試");
        _probe.Reads.Should().BeGreaterThanOrEqualTo(3, "敗者要重新讀目前版本才重試");
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Approved, "兩個分支都同意、匯合後走到結束");
    }
}
