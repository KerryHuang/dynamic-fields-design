namespace DynamicModules.Application.Common.Time;

public interface IBusinessTime
{
    DateTime Now { get; }
    DateOnly Today { get; }
}
