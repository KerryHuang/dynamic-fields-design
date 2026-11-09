using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>內建共用主檔 partner（往來對象）與 contact（聯絡人，含任職經歷明細）的套件內容（共用主檔設計 §5.1、§5.2）。</summary>
public class BuiltInMasterPackagesTests
{
    private static readonly IReadOnlyList<AppPackageData> BuiltIns = AppCatalog.Load(AppCatalog.DefaultDirectory).BuiltIns;

    private static AppPackageData Package(string id) => BuiltIns.Single(p => p.Manifest.Id == id);

    private static List<string> Fields(TemplateModule m) =>
        m.Blocks.SelectMany(b => b.Fields).Select(f => $"{f.Code}:{f.Type}".ToLowerInvariant()).ToList();

    private static TemplateField Field(TemplateModule m, string code) => m.Blocks.SelectMany(b => b.Fields).Single(f => f.Code == code);

    [Fact]
    public void Partner_IsAnExtensibleMaster_WithTheBaseFieldsOfTheDesign()
    {
        var p = Package("partner");
        p.Manifest.IsMaster.Should().BeTrue();
        p.Groups.Select(g => g.Code).Should().Equal("steward");
        var m = p.Modules.Should().ContainSingle().Which;
        (m.Code, m.Key, m.Extensible, m.TitleField).Should().Be(("partner", "master-partner", true, "name"));
        Fields(m).Should().Equal("no:autonumber", "name:text", "role:enum", "taxid:text", "phone:phone", "email:email", "address:text",
            "owner:user", "salespaymentterms:text", "purchasepaymentterms:text", "active:boolean");
        Field(m, "role").Options.Select(o => o.Code).Should().Equal("customer", "supplier", "both");
        Field(m, "role").Config.TryGetProperty("required", out _).Should().BeFalse("角色可以空白，轉換時由固定值補上");
        Field(m, "active").Config.GetProperty("defaultValue").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Partner_HasCustomerAndSupplierViews_FilteredByRole()
    {
        var views = Package("partner").Modules.Single().Views;
        views.Select(v => v.Name.Resolve("zh-TW")).Should().Equal("客戶", "供應商");
        static List<string> Roles(TemplateView v) =>
            v.Filter!.Value.GetProperty("value").EnumerateArray().Select(x => x.GetString()!).ToList();
        Roles(views[0]).Should().Equal("opt_customer", "opt_both");
        Roles(views[1]).Should().Equal("opt_supplier", "opt_both");
    }

    [Fact]
    public void Contact_RequiresPartner_KeepsTheCurrentCompany_AndHasAnEmploymentHistoryDetail()
    {
        var p = Package("contact");
        p.Manifest.IsMaster.Should().BeTrue();
        p.Manifest.Requires.Masters.Should().ContainSingle().Which.Id.Should().Be("partner");
        var contact = p.Modules.Single(m => m.Code == "contact");
        contact.Extensible.Should().BeTrue();
        Fields(contact).Should().Equal("name:text", "partner:reference", "title:text", "phone:phone", "email:email");
        var current = Field(contact, "partner").Config;
        current.GetProperty("required").GetBoolean().Should().BeTrue();
        current.GetProperty("refTarget").GetProperty("app").GetString().Should().Be("partner");

        var employment = p.Modules.Single(m => m.Code == "employment");
        employment.Extensible.Should().NotBe(true, "任職經歷不接受別的應用程式貢獻欄位");
        Fields(employment).Should().Equal("contact:reference", "partner:reference", "title:text", "startdate:date", "enddate:date");
        Field(employment, "contact").Config.GetProperty("relation").GetString().Should().Be("detail");
        Field(employment, "contact").Config.GetProperty("refTarget").GetProperty("key").GetString().Should().Be("contact");
        var company = Field(employment, "partner").Config;
        company.GetProperty("required").GetBoolean().Should().BeTrue();
        company.GetProperty("refTarget").GetProperty("app").GetString().Should().Be("partner");
    }

    [Fact]
    public void TheCatalog_HasExactlyTheTwoMasters() =>
        BuiltIns.Where(p => p.Manifest.IsMaster).Select(p => p.Manifest.Id).Should().BeEquivalentTo(["partner", "contact"]);

    [Fact]
    public void Crm_UsesTheMastersInsteadOfItsOwnCustomerAndContact()
    {
        var crm = Package("crm-sales");
        crm.Manifest.Requires.Masters.Select(r => r.Id).Should().Equal("partner", "contact");
        crm.Manifest.Requires.Apps.Should().BeEmpty();
        crm.Modules.Select(m => m.Code).Should().NotContain(["customer", "contact"]).And.Contain("product", "CRM 的產品維持原樣");
        crm.Contributions.Select(c => $"{c.Master}/{c.Module}:{c.Block.Code}").Should().Equal("partner/partner:crm", "contact/contact:crm");
        crm.Contributions[0].Fields.Select(f => f.Code).Should().Equal("industry", "fromLead");
        crm.Contributions.Should().OnlyContain(c => c.Permissions.Select(p => p.Group).SequenceEqual(new[] { "sales", "manager" }));
    }

    [Fact]
    public void ACrmSymbolThatIsNotABaseFieldOfPartner_RefusesStartup()
    {
        var dir = Directory.CreateTempSubdirectory("catalog-builtin-").FullName;
        try
        {
            foreach (var file in Directory.GetFiles(AppCatalog.DefaultDirectory, "*.app.json"))
                File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
            var crm = Path.Combine(dir, "crm-sales.app.json");
            File.WriteAllText(crm, File.ReadAllText(crm).Replace("\"partner/partner.phone\"", "\"partner/partner.fax\""));

            var act = () => AppCatalog.Load(dir);
            act.Should().Throw<InvalidOperationException>().Which.Message
                .Should().Contain("crm-sales.app.json").And.Contain("partner/partner.fax 不是主檔 partner 的基本欄位");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
