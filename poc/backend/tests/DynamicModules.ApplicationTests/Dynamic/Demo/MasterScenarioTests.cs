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
        PlanFor().Steps.Should().BeEmpty("沒有開啟任何用到主檔的應用程式");

        var mes = PlanFor(DemoCatalog.MesAppId).Created;
        mes.Should().OnlyContain(r => r.ModuleCode == MasterScenario.PartnerModule, "MES 不用聯絡人");
        mes.Should().HaveCount(8).And.OnlyContain(p => (string)p.Facts["role"]! == "customer");
        mes.Select(p => (string)p.Facts["name"]!).Should().StartWith("嵐岳精密", "沿用 MES 原本的虛構客戶名稱");

        var crm = PlanFor(DemoCatalog.CrmAppId);
        var partners = crm.Created.Where(r => r.ModuleCode == MasterScenario.PartnerModule).ToList();
        partners.Select(p => (string)p.Facts["role"]!).Distinct().Should().BeEquivalentTo(["customer", "both"], "只開 CRM 時不建供應商");
        partners.Should().HaveCount(10);

        var inventory = PlanFor(DemoCatalog.InventoryAppId).Created;
        var stock = inventory.Where(r => r.ModuleCode == MasterScenario.PartnerModule).ToList();
        stock.Select(p => (string)p.Facts["name"]!).Should().Equal(["昌盛鋼鐵", "宏達五金", "精工模料", "遠航機械", "新昇塑膠", "大成電子", "恆久工業"],
            "沿用進銷存原本的虛構供應商與客戶名稱");
        stock.Count(p => (string)p.Facts["role"]! == "supplier").Should().Be(3);
        inventory.Count(r => r.ModuleCode == MasterScenario.ContactModule).Should().Be(7, "原本的聯絡人文字改成聯絡人主檔");
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
        partners.Should().HaveCount(27, "CRM 十家、MES 八家、進銷存七家，加上兩筆由潛在客戶轉出的客戶");
        var customer = await fx.Opt(partner, "角色", "客戶");
        var both = await fx.Opt(partner, "角色", "客戶與供應商");
        var supplier = await fx.Opt(partner, "角色", "供應商");
        partners.Select(p => DemoGeneratedFixture.Text(p, role)).Should().OnlyContain(r => r == customer || r == both || r == supplier);
        partners.Count(p => DemoGeneratedFixture.Text(p, role) == supplier).Should().Be(3, "只有進銷存需要供應商");
        partners.Count(p => DemoGeneratedFixture.Text(p, industry) is not null).Should().BeGreaterThanOrEqualTo(10, "CRM 補上產業");

        var contacts = await fx.Records(contact);
        contacts.Should().HaveCount(17, "CRM 十位、進銷存七位");
        var rows = new List<int>();
        foreach (var c in contacts)
        {
            var full = (DynamicRecordResponse)(await fx.As("admin", new GetRecordRequest(c.Id))).Data!;
            rows.Add(full.Details![employment].Count);
        }
        rows.Should().OnlyContain(n => n >= 1);
        rows.Count(n => n >= 2).Should().BeGreaterThanOrEqualTo(2, "幾位聯絡人換過公司");

        (await fx.Records(contact, "sales01")).Should().HaveCount(17, "業務靠 CRM 貢獻的權限看得到聯絡人");
    }
}
