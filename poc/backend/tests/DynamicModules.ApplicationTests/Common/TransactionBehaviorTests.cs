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
}
