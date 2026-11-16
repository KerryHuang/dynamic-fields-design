using DynamicModules.Application.Dynamic.OrganizationService;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.Organization;

/// <summary>資料萬一成環（寫入時已擋，但舊資料或手改資料庫可能發生）：讀取端必須有界、不卡住。</summary>
public class DepartmentRulesCycleTests
{
    private static readonly Department[] Loop =
    [
        new() { Id = 1, Code = "a", ParentId = 2 },
        new() { Id = 2, Code = "b", ParentId = 1 },
    ];

    [Fact]
    public void DepthOf_Cycle_CountsEachDepartmentOnce()
    {
        var byId = Loop.ToDictionary(d => d.Id);

        DepartmentRules.DepthOf(byId, 1).Should().Be(2);
    }

    [Fact]
    public void HeightOf_Cycle_CountsEachDepartmentOnce()
    {
        DepartmentRules.HeightOf(Loop, 1).Should().Be(2);
    }

    [Fact]
    public void Descendants_Cycle_Terminates()
    {
        DepartmentRules.Descendants(Loop, 1).Should().BeEquivalentTo(new long[] { 2 });
    }
}
