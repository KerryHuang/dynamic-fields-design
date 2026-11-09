using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 匯出（設計 §9）：使用者套件的主檔歸 requires.masters、contributions 從資料庫重建（依歸屬、停用的帶 active: false、權限只帶自己授予的、
/// 群組代號單一來源）、目標是主檔的轉換照樣匯出；主檔匯出只有基本欄位、權限只帶 steward、孤兒不匯出並列在警告；退回代號避開基本代號。
/// </summary>
public class SharedMasterExportTests : IDisposable
{
    private const string Partner = "master-bp-partner";
    private readonly SharedMasterRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private async Task OpenCrmAndStockAsync()
    {
        await _rig.ImportAllAsync();
        await _rig.OpenOkAsync("crmx");
        await _rig.OpenOkAsync("stk");
    }

    private static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    private static bool? Active(JsonElement field) =>
        field.TryGetProperty("active", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False ? a.GetBoolean() : null;

    [Fact]
    public async Task ExportingAUser_PutsMastersInRequiresMasters_AndRebuildsItsContributionsFromTheDatabase()
    {
        await OpenCrmAndStockAsync();
        var visits = await _rig.AddDesignerFieldAsync(Partner, _rig.BlockId("crmx", "bp/partner.crm"), "enum", "拜訪頻率");
        await _rig.AddDesignerOptionAsync(visits, "每月");
        await _rig.SetFieldStatusOkAsync(_rig.FieldId("crmx", "bp/partner.memo"), "Inactive");
        var visitsCode = _rig.Db(db => db.Set<AppField>().Single(f => f.FieldId == visits).Name)["bp/partner.".Length..];

        var (crm, warnings) = await _rig.ExportAsync("crmx");
        var requires = crm.GetProperty("manifest").GetProperty("requires");
        Arr(requires, "apps").Should().BeEmpty("主檔不進 requires.apps");
        Arr(requires, "masters").Select(m => $"{Str(m, "id")}:{string.Join(",", Arr(m, "modules").Select(x => x.GetString()))}")
            .Should().Equal("bp:partner", "ct:contact");

        var contribution = Arr(crm, "contributions").Should().ContainSingle().Subject;
        Str(contribution, "master").Should().Be("bp");
        Str(contribution, "module").Should().Be("partner");
        Str(contribution.GetProperty("block"), "code").Should().Be("crm");
        var fields = Arr(contribution, "fields").ToDictionary(f => Str(f, "code"));
        fields.Keys.Should().BeEquivalentTo(["industry", "fromLead", "mainContact", "score", "memo", "headX2", visitsCode],
            "自己名下的貢獻欄位（含設計器加在 CRM 區塊的），不含基本欄位與進銷存的貢獻");
        Active(fields["memo"]).Should().BeFalse("停用的照樣匯出並標 active: false");
        Active(fields["industry"]).Should().BeNull();
        Arr(fields[visitsCode], "options").Select(o => Str(o, "code")).Should().ContainSingle().Which.Should().MatchRegex("^u[a-z0-9]{8}$");
        Arr(fields["industry"], "options").Select(o => Str(o, "code")).Should().Equal("manufacturing", "retail");
        fields["headX2"].GetProperty("config").GetProperty("formula").GetString().Should().Be("{headcount} * 2", "公式引用基本欄位用基本代號");
        var fromLead = fields["fromLead"].GetProperty("config").GetProperty("refTarget");
        Str(fromLead, "key").Should().Be("lead");
        fromLead.TryGetProperty("app", out _).Should().BeFalse();
        var mainContact = fields["mainContact"].GetProperty("config").GetProperty("refTarget");
        (Str(mainContact, "key"), Str(mainContact, "app")).Should().Be(("contact", "ct"));

        // 權限只帶 crmx 自己授予的；群組代號和模組權限同一個
        var perms = Arr(contribution, "permissions").ToList();
        perms.Should().ContainSingle();
        var salesCode = Str(perms[0], "group");
        Arr(crm, "groups").Select(g => Str(g, "code")).Should().Equal(salesCode);
        Arr(crm, "modules").SelectMany(m => Arr(m, "permissions")).Select(p => Str(p, "group")).Distinct().Should().Equal(salesCode);
        perms[0].GetProperty("privileges").GetProperty("read").GetBoolean().Should().BeTrue();
        perms[0].GetProperty("privileges").GetProperty("edit").GetBoolean().Should().BeFalse();

        // 目標是主檔的轉換照樣匯出，主檔欄位與貢獻欄位用主檔符號
        var conversion = Arr(crm, "conversions").Should().ContainSingle().Subject;
        Str(conversion, "target").Should().Be("bp/partner");
        Str(conversion.GetProperty("header"), "sourceField").Should().Be("bp/partner.fromLead");
        Arr(conversion.GetProperty("header"), "copy").Select(c => $"{Str(c, "from")}>{Str(c, "to")}")
            .Should().BeEquivalentTo("lead.company>bp/partner.name", "lead.phone>bp/partner.phone");

        // 使用者模組上的主檔符號：參照目標、守衛、帶值
        var customer = Arr(crm, "modules").Single(m => Str(m, "code") == "order")
            .GetProperty("blocks")[0].GetProperty("fields").EnumerateArray().Single(f => Str(f, "code") == "customer").GetProperty("config");
        (Str(customer.GetProperty("refTarget"), "key"), Str(customer.GetProperty("refTarget"), "app")).Should().Be(("partner", "bp"));
        Str(customer.GetProperty("refGuard"), "field").Should().Be("bp/partner.role");
        Arr(customer.GetProperty("refGuard"), "in").Select(x => x.GetString()).Should().Equal("customer", "both");
        Str(Arr(customer.GetProperty("fill"), "map").Single(), "from").Should().Be("bp/partner.phone");
        warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task AGroupCreatedAtOpen_HasOneCodeAcrossModulesContributionsAndGroups()
    {
        await _rig.ImportAsync(SharedMasterPackages.Bp);
        await _rig.ImportAsync(SharedMasterPackages.Ct);
        await _rig.ImportAsync(SharedMasterPackages.Crm.Replace("\"name\": { \"zh-TW\": \"業務\" }", "\"name\": { \"zh-TW\": \"業務二組\" }"));
        await _rig.OpenOkAsync("crmx", new
        {
            groupMap = new Dictionary<string, object> { ["sales"] = new { mode = "create" } },
            masters = new Dictionary<string, object>
            {
                ["bp"] = new Dictionary<string, object> { ["steward"] = new { mode = "create" } },
                ["ct"] = new Dictionary<string, object> { ["steward"] = new { mode = "create" } }
            }
        });
        var (crm, _) = await _rig.ExportAsync("crmx");
        var code = Arr(crm, "groups").Select(g => Str(g, "code")).Should().ContainSingle().Subject;
        Arr(crm, "modules").SelectMany(m => Arr(m, "permissions")).Select(p => Str(p, "group")).Distinct().Should().Equal(code);
        Arr(Arr(crm, "contributions").Single(), "permissions").Select(p => Str(p, "group")).Should().Equal(code);
    }

    [Fact]
    public async Task ExportingTheMaster_HasOnlyItsBaseFields_InactiveOnesMarked_StewardOnly_AndWarnsAboutOrphans()
    {
        await OpenCrmAndStockAsync();
        var grade = await _rig.AddDesignerFieldAsync(Partner, _rig.BlockId("bp", "partner.main"), "enum", "等級");
        await _rig.AddDesignerOptionAsync(grade, "金牌");
        var gradeCode = _rig.Db(db => db.Set<AppField>().Single(f => f.FieldId == grade).Name)["partner.".Length..];
        var industry = _rig.FieldId("crmx", "bp/partner.industry");
        var option = _rig.Db(db => db.Set<FieldOption>().First(o => o.FieldId == industry).Id);
        await _rig.CreateRecordAsync(_rig.Admin, Partner, new Dictionary<string, object>
        {
            [_rig.FieldKey("bp", "partner.name")] = "甲公司", [$"f_{industry}"] = $"opt_{option}"
        });
        await _rig.CloseOkAsync("crmx");
        await _rig.DeleteAppOkAsync("crmx");

        var (bp, warnings) = await _rig.ExportAsync("bp");
        Str(bp.GetProperty("manifest"), "kind").Should().Be("master");
        Arr(bp, "contributions").Should().BeEmpty();
        var module = Arr(bp, "modules").Should().ContainSingle().Subject;
        module.GetProperty("extensible").GetBoolean().Should().BeTrue();
        Str(module, "key").Should().Be(Partner);
        var blocks = Arr(module, "blocks").ToList();
        blocks.Select(b => Str(b, "code")).Should().Equal(["main"], "貢獻區塊與孤兒區塊不匯出");
        var fields = Arr(blocks[0], "fields").ToDictionary(f => Str(f, "code"));
        fields.Keys.Should().Equal("no", "name", "role", "phone", "headcount", "taxId", gradeCode);
        Active(fields["taxId"]).Should().BeFalse();
        Arr(fields[gradeCode], "options").Should().ContainSingle();
        warnings.Should().ContainSingle(w => w.Contains("產業") && w.Contains("已刪除應用程式留下的停用欄位"), string.Join("\n", warnings));

        var steward = Arr(module, "permissions").Should().ContainSingle().Subject;
        Arr(bp, "groups").Select(g => Str(g, "code")).Should().Equal(Str(steward, "group"));
        _rig.Db(db => db.Set<PermissionGroup>().Count(g => g.Key == "warehouse")).Should().Be(1, "進銷存的 warehouse 群組對主檔有權限，但不是主檔自己授予的");
    }

    /// <summary>
    /// 貢獻參照欄位指向的應用程式只有真的是主檔才收進 requires.masters（審查 P2-1）。設計器現在擋下這種欄位，
    /// 這裡直接改資料庫模擬一個指向一般應用程式模組的舊貢獻欄位。
    /// </summary>
    [Fact]
    public async Task AContributionReferenceToAnOrdinaryApp_IsNotListedInRequiresMasters()
    {
        await OpenCrmAndStockAsync();
        var ship = await _rig.AddDesignerFieldAsync(Partner, _rig.BlockId("bp", "partner.main"), "reference", "出貨單",
            new { refTarget = new { kind = "module", key = "stk-ship" }, onDelete = "setNull" });
        var crm = _rig.BlockId("crmx", "bp/partner.crm");
        var crmx = _rig.Db(db => db.Set<App>().Single(a => a.Key == "crmx").Id);
        _rig.Db(db =>
        {
            db.Set<AppField>().RemoveRange(db.Set<AppField>().Where(f => f.FieldId == ship));
            db.Set<AppField>().Add(new AppField { AppId = crmx, Name = "bp/partner.ushipment", FieldId = ship });
            db.Set<Field>().Single(f => f.Id == ship).BlockId = crm;
        });

        var (package, _) = await _rig.ExportAsync("crmx");
        Arr(package.GetProperty("manifest").GetProperty("requires"), "masters").Select(m => Str(m, "id"))
            .Should().Equal(["bp", "ct"], "stk 是一般應用程式，不是主檔");
    }

    /// <summary>
    /// 主檔匯出的選項範圍只帶 steward 群組；別的群組不會因此進 groups[]。只限定給別的群組的選項改成只限 steward，
    /// 不能整筆不帶，否則在另一個庫會變成所有人可見（只收緊、不放寬）。
    /// </summary>
    [Fact]
    public async Task ExportingTheMaster_KeepsOnlyStewardGroupsInOptionScopes()
    {
        await OpenCrmAndStockAsync();
        var partner = _rig.Db(db => db.Set<Module>().Single(m => m.Key == Partner).Id);
        var steward = _rig.Db(db => db.Set<AppGrant>().First(g => g.ModuleId == partner && g.SourceKind == GrantSource.Master).GroupId);
        var customer = _rig.Db(db => db.Set<AppOption>().Single(o => o.Name == "partner.customer").OptionId);
        var supplier = _rig.Db(db => db.Set<AppOption>().Single(o => o.Name == "partner.supplier").OptionId);
        var sales = _rig.GroupId("sales");
        _rig.Db(db => db.Set<FieldOptionScope>().AddRange(
            new FieldOptionScope { OptionId = customer, GroupId = steward }, new FieldOptionScope { OptionId = customer, GroupId = sales },
            new FieldOptionScope { OptionId = supplier, GroupId = sales }));

        var (bp, _) = await _rig.ExportAsync("bp");
        var stewardCode = Str(Arr(Arr(bp, "modules").Single(), "permissions").Single(), "group");
        Arr(bp, "optionScopes").Select(s => $"{Str(s, "option")}:{string.Join(",", Arr(s, "groups").Select(g => g.GetString()))}")
            .Should().BeEquivalentTo([$"partner.customer:{stewardCode}", $"partner.supplier:{stewardCode}"]);
        Arr(bp, "groups").Select(g => Str(g, "code")).Should().Equal(stewardCode);
    }

    [Fact]
    public async Task AFallbackOptionCode_NeverEqualsABaseOptionCode()
    {
        await OpenCrmAndStockAsync();
        var industry = _rig.FieldId("crmx", "bp/partner.industry");
        var retail = _rig.Db(db => db.Set<AppOption>().Single(o => o.Name == "bp/partner.retail").OptionId);
        // 模擬一個沒有對照列的貢獻選項（退回 o_<id>），並讓某個基本選項的代號剛好就是那個退回代號
        _rig.Db(db =>
        {
            db.Set<AppOption>().Remove(db.Set<AppOption>().Single(o => o.Name == "bp/partner.retail"));
            var both = db.Set<AppOption>().Single(o => o.Name == "partner.both");
            db.Set<AppOption>().Remove(both);
            db.Set<AppOption>().Add(new AppOption { AppId = both.AppId, Name = $"partner.o_{retail}", OptionId = both.OptionId });
        });

        var (crm, _) = await _rig.ExportAsync("crmx");
        var codes = Arr(Arr(Arr(crm, "contributions").Single(), "fields").Single(f => Str(f, "code") == "industry"), "options")
            .Select(o => Str(o, "code")).ToList();
        codes.Should().HaveCount(2).And.Contain("manufacturing").And.NotContain($"o_{retail}");
        industry.Should().BePositive();
    }
}
