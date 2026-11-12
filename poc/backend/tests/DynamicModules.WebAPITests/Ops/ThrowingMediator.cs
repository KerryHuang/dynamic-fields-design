using MediatR;

namespace DynamicModules.WebAPITests.Ops;

/// <summary>替身 IMediator：第 n 次呼叫丟出指定例外（n 從 1 起算，跨請求共用計數）。</summary>
internal sealed class ThrowingMediator(Func<int, Exception> make) : IMediator
{
    private int _calls;

    private Exception Next() => make(Interlocked.Increment(ref _calls));

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default) => throw Next();
    public Task Send<TRequest>(TRequest request, CancellationToken ct = default) where TRequest : IRequest => throw Next();
    public Task<object?> Send(object request, CancellationToken ct = default) => throw Next();
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task Publish(object notification, CancellationToken ct = default) => throw new NotSupportedException();
    public Task Publish<TNotification>(TNotification notification, CancellationToken ct = default)
        where TNotification : INotification => throw new NotSupportedException();
}
