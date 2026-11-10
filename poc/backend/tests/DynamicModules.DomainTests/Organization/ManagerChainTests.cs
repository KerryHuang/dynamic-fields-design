using DynamicModules.Domain.Organization;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Organization;

/// <summary>設計 §17.1 的共同組織：總部 gm；業務部 mgrA；業務一課 lead1；採購部空缺；財務部 fin2。</summary>
public class ManagerChainTests
{
    private const long Hq = 1, Sales = 2, Sales1 = 3, Purchasing = 4, Finance = 5;

    private static OrgSnapshot Org(Func<OrgDepartment, OrgDepartment>? tweak = null, params string[] inactiveUsers)
    {
        var departments = new[]
        {
            new OrgDepartment(Hq, null, "gm", true),
            new OrgDepartment(Sales, Hq, "mgrA", true),
            new OrgDepartment(Sales1, Sales, "lead1", true),
            new OrgDepartment(Purchasing, Hq, null, true),
            new OrgDepartment(Finance, Hq, "fin2", true),
        }.Select(d => tweak?.Invoke(d) ?? d).ToDictionary(d => d.Id);
        var members = new Dictionary<string, long>
        {
            ["gm"] = Hq,
            ["mgrA"] = Sales, ["emp1"] = Sales, ["emp2"] = Sales,
            ["lead1"] = Sales1, ["emp3"] = Sales1,
            ["buyer1"] = Purchasing, ["buyer2"] = Purchasing,
            ["fin1"] = Finance, ["fin2"] = Finance,
        };
        var active = members.Keys.Except(inactiveUsers).ToHashSet();
        return new OrgSnapshot(departments, members, active);
    }

    [Fact]
    public void Level1_DirectManager() => ManagerChain.Resolve(Org(), "emp3", 1).Should().Be("lead1");

    [Fact]
    public void Level2_FromLevel1Department() => ManagerChain.Resolve(Org(), "emp3", 2).Should().Be("mgrA");

    [Fact]
    public void ManagerIsInitiator_GoesUp() => ManagerChain.Resolve(Org(), "lead1", 1).Should().Be("mgrA");

    [Fact]
    public void VacantManager_GoesUp() => ManagerChain.Resolve(Org(), "buyer1", 1).Should().Be("gm");

    [Fact]
    public void InactiveManager_TreatedVacant() =>
        ManagerChain.Resolve(Org(null, "lead1"), "emp3", 1).Should().Be("mgrA");

    [Fact]
    public void InactiveDepartment_TreatedVacant() =>
        ManagerChain.Resolve(Org(d => d.Id == Sales1 ? d with { Active = false } : d), "emp3", 1).Should().Be("mgrA");

    [Fact]
    public void RootReached_Null()
    {
        ManagerChain.Resolve(Org(), "gm", 1).Should().BeNull();
        ManagerChain.Resolve(Org(), "emp3", 4).Should().BeNull();
    }

    [Fact]
    public void NoDepartment_Null() => ManagerChain.Resolve(Org(), "nobody", 1).Should().BeNull();

    [Fact]
    public void Level3_ReachesRoot() => ManagerChain.Resolve(Org(), "emp3", 3).Should().Be("gm");
}
