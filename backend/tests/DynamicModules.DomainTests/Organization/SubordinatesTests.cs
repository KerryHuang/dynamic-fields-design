using DynamicModules.Domain.Organization;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Organization;

/// <summary>主管範圍的部屬解析（看板設計 §4.1）：1 總部(gm) → 2 業務部(mgrA) → 3 業務一課(lead1)；4 採購部(空缺)。</summary>
public class SubordinatesTests
{
    private static OrgSnapshot Org(Func<OrgDepartment, OrgDepartment>? tweak = null, Action<Dictionary<string, long>>? members = null,
        params string[] inactiveUsers)
    {
        var departments = new[]
        {
            new OrgDepartment(1, null, "gm", true),
            new OrgDepartment(2, 1, "mgrA", true),
            new OrgDepartment(3, 2, "lead1", true),
            new OrgDepartment(4, 1, null, true),
        }.Select(d => tweak?.Invoke(d) ?? d).ToDictionary(d => d.Id);
        var map = new Dictionary<string, long>
        {
            ["gm"] = 1, ["mgrA"] = 2, ["emp1"] = 2, ["lead1"] = 3, ["emp3"] = 3, ["buyer1"] = 4,
        };
        members?.Invoke(map);
        return new OrgSnapshot(departments, map, map.Keys.Except(inactiveUsers).ToHashSet());
    }

    [Fact]
    public void Direct_MembersOfManagedDepartment_ExcludesManager()
    {
        Subordinates.Of(Org(), "mgrA", recursive: false).Should().BeEquivalentTo(["emp1"]);
    }

    [Fact]
    public void Recursive_IncludesAllDescendants()
    {
        Subordinates.Of(Org(), "mgrA", recursive: true).Should().BeEquivalentTo(["emp1", "lead1", "emp3"]);
        Subordinates.Of(Org(), "gm", recursive: true).Should().BeEquivalentTo(["mgrA", "emp1", "lead1", "emp3", "buyer1"]);
    }

    [Fact]
    public void NonRecursive_OnlyDirectDepartment()
    {
        Subordinates.Of(Org(), "gm", recursive: false).Should().BeEmpty("總部只有 gm 自己");
    }

    [Fact]
    public void Recursive_PassesThroughInactiveChild()
    {
        var org = Org(d => d.Id == 2 ? d with { Active = false, ManagerUserId = null } : d);
        Subordinates.Of(org, "gm", recursive: true).Should().Contain(["emp1", "lead1", "emp3"]);
    }

    [Fact]
    public void InactiveDepartment_ItsOwnManagerGetsNothing()
    {
        var org = Org(d => d.Id == 2 ? d with { Active = false } : d);
        Subordinates.Of(org, "mgrA", recursive: true).Should().BeEmpty();
    }

    [Fact]
    public void ManagerOfTwoDepartments_Union()
    {
        var org = Org(d => d.Id == 4 ? d with { ManagerUserId = "mgrA" } : d);
        Subordinates.Of(org, "mgrA", recursive: false).Should().BeEquivalentTo(["emp1", "buyer1"]);
    }

    [Fact]
    public void UserWithoutDepartment_NeverSubordinate()
    {
        var org = Org(members: m => m.Remove("emp1"));
        Subordinates.Of(org, "mgrA", recursive: true).Should().NotContain("emp1");
    }

    [Fact]
    public void InactiveUser_StillSubordinate()
    {
        Subordinates.Of(Org(inactiveUsers: "emp1"), "mgrA", recursive: false).Should().Contain("emp1");
    }

    [Fact]
    public void Cycle_DoesNotLoop()
    {
        var org = Org(d => d.Id == 1 ? d with { ParentId = 3 } : d);
        var act = () => Subordinates.Of(org, "gm", recursive: true);
        act.Should().NotThrow();
        act().Should().NotContain("gm");
    }

    [Fact]
    public void UnknownManager_Empty()
    {
        Subordinates.Of(Org(), "nobody", recursive: true).Should().BeEmpty();
    }
}
