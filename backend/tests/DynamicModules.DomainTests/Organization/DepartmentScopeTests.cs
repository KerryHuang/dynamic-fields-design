using DynamicModules.Domain.Organization;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Organization;

/// <summary>報表部門篩選的部門展開（看板設計 §8.2）：1 總部 → 2 業務部（停用）→ 3 業務一課；4 採購部；5 獨立根。</summary>
public class DepartmentScopeTests
{
    private static IReadOnlyDictionary<long, OrgDepartment> Departments() => new[]
    {
        new OrgDepartment(1, null, null, true),
        new OrgDepartment(2, 1, null, false),
        new OrgDepartment(3, 2, null, true),
        new OrgDepartment(4, 1, null, true),
        new OrgDepartment(5, null, null, true),
    }.ToDictionary(d => d.Id);

    [Fact]
    public void WithoutSubDepartments_ReturnsSelectedOnly()
    {
        DepartmentScope.Expand(Departments(), [2, 4], includeSub: false).Should().BeEquivalentTo(new long[] { 2, 4 });
    }

    [Fact]
    public void WithSubDepartments_IncludesAllDescendantsThroughInactive()
    {
        DepartmentScope.Expand(Departments(), [1], includeSub: true).Should().BeEquivalentTo(new long[] { 1, 2, 3, 4 });
        DepartmentScope.Expand(Departments(), [2], includeSub: true).Should().BeEquivalentTo(new long[] { 2, 3 });
    }

    [Fact]
    public void OverlappingSelection_DoesNotDuplicate()
    {
        DepartmentScope.Expand(Departments(), [1, 3], includeSub: true).Should().BeEquivalentTo(new long[] { 1, 2, 3, 4 });
    }

    [Fact]
    public void UnknownDepartment_IsKeptAsIs_ButHasNoChildren()
    {
        DepartmentScope.Expand(Departments(), [99], includeSub: true).Should().BeEquivalentTo(new long[] { 99 });
    }

    [Fact]
    public void EmptySelection_IsEmpty()
    {
        DepartmentScope.Expand(Departments(), [], includeSub: true).Should().BeEmpty();
    }

    [Fact]
    public void CyclicParents_Terminate()
    {
        var cyclic = new[] { new OrgDepartment(1, 2, null, true), new OrgDepartment(2, 1, null, true) }.ToDictionary(d => d.Id);
        DepartmentScope.Expand(cyclic, [1], includeSub: true).Should().BeEquivalentTo(new long[] { 1, 2 });
    }
}
