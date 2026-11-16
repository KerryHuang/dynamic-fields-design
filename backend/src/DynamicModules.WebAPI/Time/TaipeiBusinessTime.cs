using DynamicModules.Application.Common.Time;

namespace DynamicModules.WebAPI.Time;

public class TaipeiBusinessTime : IBusinessTime
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    public DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone);
    public DateOnly Today => DateOnly.FromDateTime(Now);
}
