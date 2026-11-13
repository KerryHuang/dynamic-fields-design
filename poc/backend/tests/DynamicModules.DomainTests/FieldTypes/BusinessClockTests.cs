using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using FluentAssertions;
using static DynamicModules.DomainTests.FieldTypes.HandlerTestKit;

namespace DynamicModules.DomainTests.FieldTypes;

/// <summary>日期時間的解析不依主機時區：無偏移＝台北牆上時間，帶偏移換到 +08:00。須在 TZ=UTC 與 TZ=Asia/Taipei 各跑一次。</summary>
public class BusinessClockTests
{
    [Theory]
    [InlineData("2026-11-04T23:00:00", "2026-11-04 23:00")]
    [InlineData("2026-11-04T23:00", "2026-11-04 23:00")]
    [InlineData("2026-11-04T15:00:00Z", "2026-11-04 23:00")]
    [InlineData("2026-11-04T23:00:00+08:00", "2026-11-04 23:00")]
    [InlineData("2026-11-04T20:00:00-05:00", "2026-11-05 09:00")]
    public void ParseWallClock_IsTaipeiWallClock(string s, string expected)
    {
        BusinessClock.ParseWallClock(s).Should().Be(DateTime.Parse(expected));
        BusinessClock.TryParseWallClock(s, out var at).Should().BeTrue();
        at.Should().Be(DateTime.Parse(expected));
    }

    [Theory]
    [InlineData("not a date")]
    [InlineData("")]
    public void TryParseWallClock_BadInput_False(string s) =>
        BusinessClock.TryParseWallClock(s, out _).Should().BeFalse();

    [Fact]
    public void DateTimeHandler_ToIndex_NoOffset_IsTaipeiWallClock() =>
        FieldTypeRegistry.CreateDefault().Get(FieldType.DateTime).ToIndex(J("\"2026-11-04T23:00:00\""), F(FieldType.DateTime))
            .Single().DateValue.Should().Be(new DateTime(2026, 11, 4, 23, 0, 0));

    [Theory]
    [InlineData("2026-11-04T15:00:00Z")]
    [InlineData("2026-11-04T23:00:00+08:00")]
    public void DateTimeHandler_ToIndex_WithOffset_ConvertsToTaipei(string s) =>
        FieldTypeRegistry.CreateDefault().Get(FieldType.DateTime).ToIndex(J($"\"{s}\""), F(FieldType.DateTime))
            .Single().DateValue.Should().Be(new DateTime(2026, 11, 4, 23, 0, 0));
}
