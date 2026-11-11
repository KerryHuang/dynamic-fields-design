using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Behaviors;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;

namespace DynamicModules.ApplicationTests.Common;

/// <summary>執行期再驗的系統管理員警示：只在最外層請求處理；送警示失敗不改變原本的回應。</summary>
public class ApprovalAlertBehaviorTests
{
    private record ApproveCommand : IRequest<ServiceResult>;

    private sealed class FakeSender(Func<object, object?> handle) : ISender
    {
        public List<object> Sent { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)
        {
            Sent.Add(request);
            return Task.FromResult((TResponse)handle(request)!);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken ct = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeUow : IUnitOfWork
    {
        public int Discards { get; private set; }
        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;
        public void Discard() => Discards++;
    }

    private sealed class FakeTransactions(bool active) : ITransactionManager
    {
        public bool IsActive => active;
        public Task<ITransactionScopeHandle> BeginAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private static (ApprovalAlertBehavior<ApproveCommand, ServiceResult> Behavior, ApprovalAlert Alert, FakeSender Sender, FakeUow Uow) Build(
        bool nested, Func<object, object?>? handle = null)
    {
        var alert = new ApprovalAlert();
        var sender = new FakeSender(handle ?? (_ => ServiceResult.Success()));
        var uow = new FakeUow();
        return (new ApprovalAlertBehavior<ApproveCommand, ServiceResult>(alert, sender, uow, new FakeTransactions(nested),
            NullLogger<ApprovalAlertBehavior<ApproveCommand, ServiceResult>>.Instance), alert, sender, uow);
    }

    private static Task<ServiceResult> Fail(ApprovalAlertBehavior<ApproveCommand, ServiceResult> b, ApprovalAlert alert) =>
        b.Handle(new ApproveCommand(), () =>
        {
            alert.Raise(7, "原因");
            return Task.FromResult(ServiceResult.Conflict("x"));
        }, CancellationToken.None);

    [Fact]
    public async Task Outermost_SendsAlert()
    {
        var (b, alert, sender, uow) = Build(nested: false);
        (await Fail(b, alert)).IsConflict.Should().BeTrue();
        sender.Sent.Should().ContainSingle().Which.Should().Be(new WriteApprovalAlertRequest(7, "原因"));
        uow.Discards.Should().Be(1);
        alert.Pending.Should().BeNull();
    }

    [Fact]
    public async Task Nested_KeepsPendingForOuter()
    {
        // 外層交易還開著：不丟外層的追蹤、不在外層交易裡寫警示，保留 Pending 交給最外層
        var (b, alert, sender, uow) = Build(nested: true);
        (await Fail(b, alert)).IsConflict.Should().BeTrue();
        sender.Sent.Should().BeEmpty();
        uow.Discards.Should().Be(0);
        alert.Pending.Should().Be((7L, "原因"));
    }

    [Fact]
    public async Task AlertThrows_LogsAndReturnsOriginal409()
    {
        var (b, alert, _, _) = Build(nested: false, _ => throw new InvalidOperationException("資料庫忙碌"));
        var res = await Fail(b, alert);
        res.IsConflict.Should().BeTrue();
        res.Message.Should().Be("x");
    }
}
