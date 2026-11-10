using FluentAssertions;
using MediatR;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Behaviors;

namespace DynamicModules.ApplicationTests.Common;

public class TransactionBehaviorTests
{
    private record WriteCommand : IRequest<ServiceResult>, ITransactionalRequest;
    private record ReadQuery : IRequest<ServiceResult>;

    private class FakeHandle(List<string> log) : ITransactionScopeHandle
    {
        public Task CommitAsync(CancellationToken ct) { log.Add("commit"); return Task.CompletedTask; }
        public Task RollbackAsync(CancellationToken ct) { log.Add("rollback"); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private class FakeManager(List<string> log) : ITransactionManager
    {
        public Task<ITransactionScopeHandle> BeginAsync(CancellationToken ct)
        {
            log.Add("begin");
            return Task.FromResult<ITransactionScopeHandle>(new FakeHandle(log));
        }
    }

    [Fact]
    public async Task Success_Commits()
    {
        var log = new List<string>();
        var b = new TransactionBehavior<WriteCommand, ServiceResult>(new FakeManager(log), new AfterCommitActions(Microsoft.Extensions.Logging.Abstractions.NullLogger<AfterCommitActions>.Instance));
        await b.Handle(new WriteCommand(), () => Task.FromResult(ServiceResult.Success()), CancellationToken.None);
        log.Should().Equal("begin", "commit");
    }

    [Fact]
    public async Task FailureResult_RollsBack()
    {
        var log = new List<string>();
        var b = new TransactionBehavior<WriteCommand, ServiceResult>(new FakeManager(log), new AfterCommitActions(Microsoft.Extensions.Logging.Abstractions.NullLogger<AfterCommitActions>.Instance));
        await b.Handle(new WriteCommand(), () => Task.FromResult(ServiceResult.Conflict("x")), CancellationToken.None);
        log.Should().Equal("begin", "rollback");
    }

    [Fact]
    public async Task Exception_RollsBackAndRethrows()
    {
        var log = new List<string>();
        var b = new TransactionBehavior<WriteCommand, ServiceResult>(new FakeManager(log), new AfterCommitActions(Microsoft.Extensions.Logging.Abstractions.NullLogger<AfterCommitActions>.Instance));
        var act = () => b.Handle(new WriteCommand(), () => throw new InvalidOperationException("boom"), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
        log.Should().Equal("begin", "rollback");
    }

    [Fact]
    public async Task UnmarkedRequest_SkipsTransaction()
    {
        var log = new List<string>();
        var b = new TransactionBehavior<ReadQuery, ServiceResult>(new FakeManager(log), new AfterCommitActions(Microsoft.Extensions.Logging.Abstractions.NullLogger<AfterCommitActions>.Instance));
        await b.Handle(new ReadQuery(), () => Task.FromResult(ServiceResult.Success()), CancellationToken.None);
        log.Should().BeEmpty();
    }

    private sealed class CaptureLogger : Microsoft.Extensions.Logging.ILogger<AfterCommitActions>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    private static TransactionBehavior<WriteCommand, ServiceResult> Behavior(List<string> log, AfterCommitActions actions) => new(new FakeManager(log), actions);

    [Fact]
    public async Task AfterCommit_RunsOnlyAfterCommit_AndIsDroppedOnRollback()
    {
        var log = new List<string>();
        var actions = new AfterCommitActions(Microsoft.Extensions.Logging.Abstractions.NullLogger<AfterCommitActions>.Instance);

        await Behavior(log, actions).Handle(new WriteCommand(), () =>
        {
            actions.Add("a", () => log.Add("action-a"));
            return Task.FromResult(ServiceResult.Success());
        }, CancellationToken.None);
        await Behavior(log, actions).Handle(new WriteCommand(), () =>
        {
            actions.Add("b", () => log.Add("action-b"));
            return Task.FromResult(ServiceResult.Conflict("x"));
        }, CancellationToken.None);

        log.Should().Equal("begin", "commit", "action-a", "begin", "rollback");
    }

    [Fact]
    public async Task AfterCommit_ActionThrows_LogsWarning_RestStillRun_ResultUnchanged()
    {
        var log = new List<string>();
        var logger = new CaptureLogger();
        var actions = new AfterCommitActions(logger);

        var result = await Behavior(log, actions).Handle(new WriteCommand(), () =>
        {
            actions.Add("壞掉的", () => throw new IOException("檔案被鎖住"));
            actions.Add("好的", () => log.Add("action-ok"));
            return Task.FromResult(ServiceResult.Success());
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue("資料已提交，收尾失敗不能改變結果");
        log.Should().Equal("begin", "commit", "action-ok");
        logger.Warnings.Should().ContainSingle(w => w.Contains("壞掉的"));
    }

    [Fact]
    public async Task AfterCommit_Nested_InnerCommitDoesNotRun_OuterRunsOnce()
    {
        var log = new List<string>();
        var actions = new AfterCommitActions(Microsoft.Extensions.Logging.Abstractions.NullLogger<AfterCommitActions>.Instance);
        var outer = Behavior(log, actions);
        var inner = Behavior(log, actions);

        await outer.Handle(new WriteCommand(), async () =>
        {
            actions.Add("outer", () => log.Add("action-outer"));
            await inner.Handle(new WriteCommand(), () =>
            {
                actions.Add("inner", () => log.Add("action-inner"));
                return Task.FromResult(ServiceResult.Success());
            }, CancellationToken.None);
            log.Add("after-inner");
            return ServiceResult.Success();
        }, CancellationToken.None);

        log.Should().Equal("begin", "begin", "commit", "after-inner", "commit", "action-outer", "action-inner");
    }

    [Fact]
    public async Task AfterCommit_Nested_OuterFails_DropsInnerActionsEvenThoughInnerCommitted()
    {
        var log = new List<string>();
        var actions = new AfterCommitActions(Microsoft.Extensions.Logging.Abstractions.NullLogger<AfterCommitActions>.Instance);
        var outer = Behavior(log, actions);
        var inner = Behavior(log, actions);

        await outer.Handle(new WriteCommand(), async () =>
        {
            await inner.Handle(new WriteCommand(), () =>
            {
                actions.Add("inner", () => log.Add("action-inner"));
                return Task.FromResult(ServiceResult.Success());
            }, CancellationToken.None);
            return ServiceResult.Conflict("外層失敗");
        }, CancellationToken.None);

        log.Should().NotContain("action-inner");
        log.Should().Equal("begin", "begin", "commit", "rollback");
    }

    [Fact]
    public async Task AfterCommit_RegisteredOutsideTransaction_NeverRuns_EvenIfALaterTransactionCommits()
    {
        var log = new List<string>();
        var actions = new AfterCommitActions(Microsoft.Extensions.Logging.Abstractions.NullLogger<AfterCommitActions>.Instance);
        actions.Add("交易外", () => log.Add("action-outside"));

        await Behavior(log, actions).Handle(new WriteCommand(), () => Task.FromResult(ServiceResult.Success()), CancellationToken.None);

        log.Should().Equal("begin", "commit");
    }
}
