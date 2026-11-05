using DynamicModules.Application.Common.Time;

namespace DynamicModules.Application.Common.Acting;

/// <summary>ActingScope 有指定時間時用它，否則交給原實作。</summary>
public sealed class ActingBusinessTime(IBusinessTime inner) : IBusinessTime
{
    public DateTime Now => ActingScope.Now ?? inner.Now;
    public DateOnly Today => DateOnly.FromDateTime(Now);
}
