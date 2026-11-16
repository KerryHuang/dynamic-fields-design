using System.Diagnostics;

namespace DynamicModules.WebAPI.Ops;

/// <summary>
/// 不採用外來的 traceparent／baggage（匿名使用者不該能指定我們的 request id）；
/// 對外送出仍用預設格式。<c>Ops:TrustIncomingTraceParent</c> 為 true 時不註冊這個類別。
/// </summary>
public sealed class IgnoreIncomingTraceContext : DistributedContextPropagator
{
    private readonly DistributedContextPropagator _inner = CreateDefaultPropagator();

    public override IReadOnlyCollection<string> Fields => _inner.Fields;

    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter) =>
        _inner.Inject(activity, carrier, setter);

    public override void ExtractTraceIdAndState(object? carrier, PropagatorGetterCallback? getter, out string? traceId, out string? traceState)
    {
        traceId = null;
        traceState = null;
    }

    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(object? carrier, PropagatorGetterCallback? getter) => null;
}
