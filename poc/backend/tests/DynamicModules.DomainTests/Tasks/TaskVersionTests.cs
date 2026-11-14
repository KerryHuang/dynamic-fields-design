using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Tasks;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Tasks;

/// <summary>任務版本字串要在寫入當下（記憶體裡的值）與從資料庫讀回（Kind 變 Unspecified）時相同，否則剛拿到的版本一送出就 409。</summary>
public class TaskVersionTests
{
    [Fact]
    public void SameWallClock_AnyKind_SameVersion()
    {
        var wall = new DateTime(2026, 11, 4, 9, 30, 15, 123).AddTicks(4567);

        var unspecified = TaskVersion.Of(new TaskItem { CreatedTime = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified) });

        unspecified.Should().Be("20261104093015123");
        TaskVersion.Of(new TaskItem { CreatedTime = DateTime.SpecifyKind(wall, DateTimeKind.Local) }).Should().Be(unspecified);
        TaskVersion.Of(new TaskItem { CreatedTime = DateTime.SpecifyKind(wall, DateTimeKind.Utc) }).Should().Be(unspecified);
        TaskVersion.Of(new TaskItem { CreatedTime = wall, ModifiedTime = wall.AddMilliseconds(1) }).Should().Be("20261104093015124");
    }
}
