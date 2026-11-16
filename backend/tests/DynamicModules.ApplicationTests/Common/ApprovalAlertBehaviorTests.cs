using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Behaviors;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging;
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

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static (ApprovalAlertBehavior<ApproveCommand, ServiceResult> Behavior, ApprovalAlert Alert, FakeSender Sender, FakeUow Uow) Build(
        bool nested, Func<object, object?>? handle = null, ILogger<ApprovalAlertBehavior<ApproveCommand, ServiceResult>>? logger = null)
    {
        var alert = new ApprovalAlert();
        var sender = new FakeSender(handle ?? (_ => ServiceResult.Success()));
        var uow = new FakeUow();
        return (new ApprovalAlertBehavior<ApproveCommand, ServiceResult>(alert, sender, uow, new FakeTransactions(nested),
            logger ?? NullLogger<ApprovalAlertBehavior<ApproveCommand, ServiceResult>>.Instance), alert, sender, uow);
    }

    private static Task<ServiceResult> Fail(ApprovalAlertBehavior<ApproveCommand, ServiceResult> b, ApprovalAlert alert) =>
        b.Handle(new ApproveCommand(), () =>
        {
            alert.Raise(7, "原因");
            return Task.FromResult(ServiceResult.Conflict("x"));
        }, CancellationToken.None);

    private static readonly ApprovalInstance Rolled = new() { Id = 7, RecordId = 3, ModuleId = 2, TitleSnapshot = "單據" };

    [Fact]
    public async Task Outermost_RaiseWithInstance_SendsSnapshotForRolledBackInstance()
    {
        // 實例是這次請求才建的、主交易回滾後資料庫裡已經沒有它：警示帶快照，handler 才組得出通知與連結（J20）
        var (b, alert, sender, _) = Build(nested: false);
        var res = await b.Handle(new ApproveCommand(), () =>
        {
            alert.Raise(Rolled, "原因");
            return Task.FromResult(ServiceResult.Conflict("x"));
        }, CancellationToken.None);

        res.IsConflict.Should().BeTrue();
        sender.Sent.Should().ContainSingle().Which.Should().Be(new WriteApprovalAlertRequest(7, "原因", new ApprovalAlertSnapshot(3, 2, "單據")));
        alert.Pending.Should().BeNull();
        alert.Snapshot.Should().BeNull();
    }

    [Fact]
    public async Task AlertResultFails_Logged_AndReturnsOriginalResponse()
    {
        // 警示寫入回失敗結果（找不到實例等）不是例外，過去完全沒有紀錄；現在要記 log，使用者照樣收到原本的回應
        var logger = new CapturingLogger<ApprovalAlertBehavior<ApproveCommand, ServiceResult>>();
        var (b, alert, _, _) = Build(nested: false, _ => ServiceResult.NotFound("找不到"), logger);
        var res = await b.Handle(new ApproveCommand(), () =>
        {
            alert.Raise(Rolled, "原因");
            return Task.FromResult(ServiceResult.Conflict("x"));
        }, CancellationToken.None);

        res.IsConflict.Should().BeTrue();
        res.Message.Should().Be("x");
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error && e.Message.Contains("7") && e.Message.Contains("原因"));
    }

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
