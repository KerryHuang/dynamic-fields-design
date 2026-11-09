using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Application.Dynamic.DemoService.Scenarios;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

/// <summary>
/// Demo 的共用主檔情境（共用主檔設計 §11.5）：只建立已開啟的應用程式需要的角色；聯絡人掛在客戶公司下，幾位換過公司、任職經歷兩筆以上；
/// CRM 的客戶、潛在客戶轉出的客戶都在往來對象裡。
/// </summary>
[Collection(DemoGeneratedCollection.Name)]
public class MasterScenarioTests(DemoGeneratedFixture fx)
{
    private static DemoPlan PlanFor(params string[] openApps)
    {
        var plan = new DemoPlan(DemoTestHost.Today, new Random(1), new Dictionary<string, IReadOnlyList<DemoRecord>>());
        new MasterScenario(openApps).Plan(plan);
        return plan;
    }

    [Fact]
    public void OnlyTheRolesTheOpenAppsNeed_AreCreated()
    {
        PlanFor(DemoCatalog.MesAppId, DemoCatalog.InventoryAppId).Steps.Should().BeEmpty("MES、進銷存還沒改用主檔");

        var crm = PlanFor(DemoCatalog.CrmAppId);
        var partners = crm.Created.Where(r => r.ModuleCode == MasterScenario.PartnerModule).ToList();
        partners.Select(p => (string)p.Facts["role"]!).Distinct().Should().BeEquivalentTo(["customer", "both"], "只開 CRM 時不建供應商");
        partners.Should().HaveCount(10);
    }

    [Fact]
    public void Contacts_HangUnderCustomers_AndSomeChangedCompanies()
    {
        var contacts = PlanFor(DemoCatalog.CrmAppId).Created.Where(r => r.ModuleCode == MasterScenario.ContactModule).ToList();
        contacts.Should().HaveCount(10);
        contacts.Count(c => (int)c.Facts["employment"]! >= 2).Should().BeGreaterThanOrEqualTo(2);
        contacts.Should().Contain(c => (int)c.Facts["employment"]! == 3, "有人待過兩家以上");
    }

    [Fact]
    public async Task Generated_PartnersAreCustomersWithTheCrmIndustry_AndContactsKeepTheirEmploymentHistory()
    {
        fx.Generated();
        const string partner = "master-partner", contact = "master-contact", employment = "master-employment";
        var role = await fx.Key(partner, "角色");
        var industry = await fx.Key(partner, "產業");
        var partners = await fx.Records(partner);
        partners.Should().HaveCount(12, "十家客戶加上兩筆由潛在客戶轉出的客戶");
        var customer = await fx.Opt(partner, "角色", "客戶");
        var both = await fx.Opt(partner, "角色", "客戶與供應商");
        partners.Select(p => DemoGeneratedFixture.Text(p, role)).Should().OnlyContain(r => r == customer || r == both);
        partners.Count(p => DemoGeneratedFixture.Text(p, industry) is not null).Should().BeGreaterThanOrEqualTo(10, "CRM 補上產業");

        var contacts = await fx.Records(contact);
        contacts.Should().HaveCount(10);
        var rows = new List<int>();
        foreach (var c in contacts)
        {
            var full = (DynamicRecordResponse)(await fx.As("admin", new GetRecordRequest(c.Id))).Data!;
            rows.Add(full.Details![employment].Count);
        }
        rows.Should().OnlyContain(n => n >= 1);
        rows.Count(n => n >= 2).Should().BeGreaterThanOrEqualTo(2, "幾位聯絡人換過公司");

        (await fx.Records(contact, "sales01")).Should().HaveCount(10, "業務靠 CRM 貢獻的權限看得到聯絡人");
    }
}
