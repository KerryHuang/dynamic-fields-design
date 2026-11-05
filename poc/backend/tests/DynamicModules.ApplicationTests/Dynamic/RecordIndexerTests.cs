using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

public class RecordIndexerTests
{
    [Theory]
    [InlineData(FieldType.Formula)]
    [InlineData(FieldType.Rollup)]
    public void ShouldIndex_ComputedFields_FollowFilterableAndSortable(FieldType type)
    {
        RecordIndexer.ShouldIndex(new Field { Type = type, Config = new FieldConfig { IsFilterable = true } }).Should().BeTrue();
        RecordIndexer.ShouldIndex(new Field { Type = type, Config = new FieldConfig { IsSortable = true } }).Should().BeTrue();
        RecordIndexer.ShouldIndex(new Field { Type = type, Config = new FieldConfig() }).Should().BeFalse();
    }
}
