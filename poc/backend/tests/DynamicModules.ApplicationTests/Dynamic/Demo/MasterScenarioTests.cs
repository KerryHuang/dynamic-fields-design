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

    /// <summary>規劃階段的 <see cref="MasterScenario.ForApp"/>：<see cref="DemoPlan.Records"/> 要等整份時間軸合併後才有，這裡直接讀這個情境建的記錄。</summary>
    private static List<DemoRecord> Of(DemoPlan plan, string appId) =>
        plan.Created.Where(r => r.ModuleCode == MasterScenario.PartnerModule && ((string[])r.Facts["apps"]!).Contains(appId)).ToList();

    [Fact]
    public void OnlyTheRolesTheOpenAppsNeed_AreCreated()
    {
        PlanFor().Steps.Should().BeEmpty("沒有開啟任何用到主檔的應用程式");

        var mes = PlanFor(DemoCatalog.MesAppId).Created;
        mes.Should().OnlyContain(r => r.ModuleCode == MasterScenario.PartnerModule, "MES 不用聯絡人");
        mes.Should().HaveCount(8).And.OnlyContain(p => (string)p.Facts["role"]! == "customer");
        mes.Select(p => (string)p.Facts["name"]!).Should().Equal(["晨曦機械", "遠見電子", "新航物流", "綠野食品", "嵐岳精密", "恆川機械", "曜陽電子", "岳峰精機"],
            "前四家與 CRM 共用，後四家沿用 MES 原本的虛構客戶名稱");

        var crm = PlanFor(DemoCatalog.CrmAppId);
        var partners = crm.Created.Where(r => r.ModuleCode == MasterScenario.PartnerModule).ToList();
        partners.Select(p => (string)p.Facts["role"]!).Distinct().Should().BeEquivalentTo(["customer", "both"], "只開 CRM 時不建供應商");
        partners.Should().HaveCount(10);

        var inventory = PlanFor(DemoCatalog.InventoryAppId).Created;
        var stock = inventory.Where(r => r.ModuleCode == MasterScenario.PartnerModule).ToList();
        stock.Select(p => (string)p.Facts["name"]!).Should().Equal(["晨曦機械", "遠見電子", "新航物流", "綠野食品", "昌盛鋼鐵", "宏達五金", "精工模料", "恆久工業"],
            "三家供應商與一家只有進銷存用的客戶沿用原本的虛構名稱，其餘四家客戶由 CRM 的客戶兼任");
        stock.Count(p => (string)p.Facts["role"]! == "supplier").Should().Be(3);
        inventory.Count(r => r.ModuleCode == MasterScenario.ContactModule).Should().Be(8, "原本的聯絡人文字改成聯絡人主檔");
    }

    [Fact]
    public void WithAllThreeApps_FourCrmCustomersAreAlsoMesAndInventoryCustomers_EighteenCompaniesInTotal()
    {
        var plan = PlanFor(DemoCatalog.MesAppId, DemoCatalog.CrmAppId, DemoCatalog.InventoryAppId);
        var partners = plan.Created.Where(r => r.ModuleCode == MasterScenario.PartnerModule).ToList();
        partners.Should().HaveCount(18, "25 家降到 18 家");
        partners.Select(p => (string)p.Facts["name"]!).Should().OnlyHaveUniqueItems();

        var crm = Of(plan, DemoCatalog.CrmAppId);
        var mes = Of(plan, DemoCatalog.MesAppId);
        var inventory = Of(plan, DemoCatalog.InventoryAppId);
        crm.Should().HaveCount(10);
        mes.Should().HaveCount(8);
        inventory.Should().HaveCount(8);
        crm.Intersect(mes).Select(p => (string)p.Facts["name"]!).Should().Equal("晨曦機械", "遠見電子", "新航物流", "綠野食品");
        crm.Intersect(inventory).Should().HaveCount(4);
        mes.Intersect(inventory).Should().HaveCount(4);

        // 同一筆往來對象只建一次，早於所有應用程式的第一張引用它的單據
        partners.Should().OnlyContain(p => p.Facts.ContainsKey("apps"));
        plan.Created.Count(r => r.ModuleCode == MasterScenario.ContactModule).Should().Be(18, "CRM 十位、進銷存八位");
    }

    [Fact]
    public void Owners_SalesOwnTheCompaniesCrmOrMesUse_InventoryOnlyCustomersBelongToTheInventoryManager()
    {
        var all = PlanFor(DemoCatalog.MesAppId, DemoCatalog.CrmAppId, DemoCatalog.InventoryAppId);
        Of(all, DemoCatalog.CrmAppId).Concat(Of(all, DemoCatalog.MesAppId)).Select(p => (string)p.Facts["owner"]!)
            .Should().OnlyContain(o => o == "sales01" || o == "sales02");
        Of(all, DemoCatalog.InventoryAppId).Where(p => (string)p.Facts["role"]! == "supplier").Select(p => (string)p.Facts["owner"]!)
            .Should().OnlyContain(o => o == "buyer01");

        Of(all, DemoCatalog.InventoryAppId).Single(p => (string)p.Facts["name"]! == "恆久工業").Facts["owner"].Should().Be("ops01", "只有進銷存用的客戶由進銷存主管負責");

        var onlyInventory = Of(PlanFor(DemoCatalog.InventoryAppId), DemoCatalog.InventoryAppId);
        onlyInventory.Where(p => (string)p.Facts["role"]! == "customer").Select(p => (string)p.Facts["owner"]!)
            .Should().HaveCount(5).And.OnlyContain(o => o == "ops01", "只開進銷存時客戶的負責人是進銷存主管，不是採購人員");
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
        partners.Should().HaveCount(20, "共 18 家（CRM 十家，其中四家兼 MES 與進銷存客戶；MES 另四家、進銷存另三家供應商與一家客戶），加上兩筆由潛在客戶轉出的客戶");
        var customer = await fx.Opt(partner, "角色", "客戶");
        var both = await fx.Opt(partner, "角色", "客戶與供應商");
        var supplier = await fx.Opt(partner, "角色", "供應商");
        partners.Select(p => DemoGeneratedFixture.Text(p, role)).Should().OnlyContain(r => r == customer || r == both || r == supplier);
        partners.Count(p => DemoGeneratedFixture.Text(p, role) == supplier).Should().Be(3, "只有進銷存需要供應商");
        partners.Count(p => DemoGeneratedFixture.Text(p, industry) is not null).Should().BeGreaterThanOrEqualTo(10, "CRM 補上產業");

        var contacts = await fx.Records(contact);
        contacts.Should().HaveCount(18, "CRM 十位、進銷存八位");
        var rows = new List<int>();
        foreach (var c in contacts)
        {
            var full = (DynamicRecordResponse)(await fx.As("admin", new GetRecordRequest(c.Id))).Data!;
            rows.Add(full.Details![employment].Count);
        }
        rows.Should().OnlyContain(n => n >= 1);
        rows.Count(n => n >= 2).Should().BeGreaterThanOrEqualTo(2, "幾位聯絡人換過公司");

        (await fx.Records(contact, "sales01")).Should().HaveCount(18, "業務靠 CRM 貢獻的權限看得到聯絡人");
    }
}
