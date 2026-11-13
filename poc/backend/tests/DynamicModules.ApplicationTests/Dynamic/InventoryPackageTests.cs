using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>
/// 內建應用程式「進銷存」（apps/inventory.app.json）的載入驗證：AppCatalog 啟動載入通過（含 V3、V4 引用層、
/// 套件內事件圖無環、allowHiddenSource、列印範本欄位引用），以及權限與四語系這些設計上的約定。
/// </summary>
public class InventoryPackageTests
{
    private static readonly Lazy<AppPackageData> Package = new(() =>
        AppCatalog.Load(AppCatalog.DefaultDirectory).BuiltIns.Single(p => p.Manifest.Id == "inventory"));

    [Fact]
    public void Catalog_LoadsInventory_AndTheValidatorReportsNoErrors()
    {
        var p = Package.Value;
        p.FormatVersion.Should().Be(8, "貢獻欄位要格式 6，採購單簽核流程要格式 8");
        p.Manifest.Requires.Apps.Should().BeEmpty("進銷存不依賴其他應用程式");
        p.Manifest.Requires.Entities.Should().BeEmpty();
        AppPackageValidator.Validate(p).Errors.Should().BeEmpty();
        p.Modules.Should().HaveCount(11, "供應商、客戶改用共用主檔往來對象");
        p.Modules.Should().OnlyContain(m => m.Key.StartsWith("inv-"));
    }

    [Fact]
    public void SuppliersAndCustomers_AreThePartnerMaster_AndContactsTheContactMaster()
    {
        // s2d-1f（共用主檔設計 §11.3）：供應商、客戶改為往來對象（角色供應商或客戶），聯絡人改用共用主檔聯絡人
        var p = Package.Value;
        p.Manifest.Requires.Masters.Select(r => $"{r.Id}:{string.Join(',', r.Modules)}").Should().Equal("partner:partner", "contact:contact");
        p.Modules.Select(m => m.Code).Should().NotContain(["supplier", "customer"]).And.Contain("item", "品項主檔是 s2d-2，這段不動");
        foreach (var (module, field, roles) in new[]
                 {
                     ("po", "supplier", new[] { "supplier", "both" }), ("receipt", "supplier", new[] { "supplier", "both" }),
                     ("shipment", "customer", new[] { "customer", "both" })
                 })
        {
            var config = p.Modules.Single(m => m.Code == module).Blocks.SelectMany(b => b.Fields).Single(f => f.Code == field).Config;
            var target = config.GetProperty("refTarget");
            (target.GetProperty("kind").GetString(), target.GetProperty("key").GetString(), target.GetProperty("app").GetString())
                .Should().Be(("module", "partner", "partner"), $"{module}.{field}");
            config.GetProperty("refGuard").GetProperty("field").GetString().Should().Be("partner/partner.role");
            config.GetProperty("refGuard").GetProperty("in").EnumerateArray().Select(x => x.GetString()).Should().Equal(roles, $"{module}.{field}");
        }

        p.Contributions.Select(c => $"{c.Master}/{c.Module}:{c.Block.Code}").Should().Equal("partner/partner:inventory", "contact/contact:inventory");
        p.Contributions[0].Block.Name.Resolve("zh-TW").Should().Be("進銷存");
        p.Contributions[0].Fields.Select(f => $"{f.Code}:{f.Type}").Should().Equal("shipAddress:Text");
        p.Contributions[1].Fields.Should().BeEmpty("聯絡人只授予權限");
        foreach (var c in p.Contributions)
        {
            c.Permissions.Select(x => x.Group).Should().Equal("buyer", "keeper", "manager");
            c.Permissions.Should().OnlyContain(x => x.Privileges.Read && x.Privileges.Pricing && !x.Privileges.Delete && !x.Privileges.Approve,
                "主檔的刪除與核准留給 steward");
            c.Permissions.Single(x => x.Group == "keeper").Privileges.Should().Match<PrivilegesDto>(x => !x.Add && !x.Edit, "倉管原本只讀供應商與客戶");
        }
    }

    [Fact]
    public void EventsChecksLedgerConversionReportsAndPrint_AreAllThere_WithAppPrefixedCodes()
    {
        var p = Package.Value;
        p.Events.Select(e => e.Code).Should().BeEquivalentTo(
            "inv-receipt-post", "inv-receipt-cancel", "inv-shipment-post", "inv-shipment-cancel", "inv-adjust-post", "inv-adjust-cancel");
        p.Checks.Should().ContainSingle().Which.Code.Should().Be("inv-no-negative-stock");
        p.Ledgers.Should().ContainSingle().Which.Module.Should().Be("move");
        p.Conversions.Should().ContainSingle().Which.Code.Should().Be("inv-po-to-receipt");
        p.Reports.Should().HaveCount(5);
        p.PrintTemplates.Should().ContainSingle().Which.Code.Should().Be("inv-ship-sheet");
        // 效果只寫入庫存異動，而庫存異動沒有任何事件：事件圖無環（validator 也會擋，這裡把設計前提釘住）
        p.Events.SelectMany(e => e.Effects).Should().OnlyContain(x => x.Target == "move");
        p.Events.Should().NotContain(e => e.Source == "move");
    }

    [Fact]
    public void NoGroupCanAddMoves_AndEveryGroupHasPricing()
    {
        var p = Package.Value;
        var move = p.Modules.Single(m => m.Code == "move");
        move.Permissions.Select(x => x.Group).Should().BeEquivalentTo("buyer", "keeper", "manager");
        move.Permissions.Should().OnlyContain(x => !x.Privileges.Add && !x.Privileges.Edit && !x.Privileges.Delete && x.Privileges.Read);
        p.Modules.SelectMany(m => m.Permissions).Where(x => x.Group != "viewer")
            .Should().OnlyContain(x => x.Privileges.Pricing, "會開單的三個群組都有金額權限，帶值才帶得到單價");
        // 查詢群組只讀品項與倉庫（夥伴整合選品項用），不開單，也不看金額
        p.Modules.Where(m => m.Permissions.Any(x => x.Group == "viewer")).Select(m => m.Code).Should().BeEquivalentTo("item", "warehouse");
        p.Modules.SelectMany(m => m.Permissions).Where(x => x.Group == "viewer")
            .Should().OnlyContain(x => x.Privileges.Read && !x.Privileges.Add && !x.Privileges.Edit && !x.Privileges.Delete && !x.Privileges.Pricing);
        p.FieldAccess.Should().BeEmpty("事件來源欄位沒有被隱藏，不需要 allowHiddenSource");
    }

    [Fact]
    public void EveryLabel_HasAllFourLocales()
    {
        var p = Package.Value;
        var texts = new List<(string Where, I18nText Text)> { ("manifest.name", p.Manifest.Name), ("manifest.description", p.Manifest.Description!) };
        texts.AddRange(p.Groups.Select(g => ($"group {g.Code}", g.Name)));
        foreach (var m in p.Modules)
        {
            texts.Add(($"{m.Code}", m.Name));
            foreach (var b in m.Blocks)
            {
                texts.Add(($"{m.Code}.{b.Code}", b.Label));
                foreach (var f in b.Fields)
                {
                    texts.Add(($"{m.Code}.{f.Code}", f.Label));
                    texts.AddRange(f.Options.Select(o => ($"{m.Code}.{f.Code}.{o.Code}", o.Label)));
                }
            }
            texts.AddRange(m.Rules.Where(r => r.Message is not null).Select(r => ($"{m.Code} rule", r.Message!)));
        }
        texts.AddRange(p.Events.Select(e => ($"event {e.Code}", e.Name)));
        texts.AddRange(p.Checks.SelectMany(c => new[] { ($"check {c.Code}", c.Name), ($"check {c.Code} message", c.Message) }));
        texts.AddRange(p.Conversions.Select(c => ($"conversion {c.Code}", c.Name)));
        texts.AddRange(p.PrintTemplates.Select(t => ($"print {t.Code}", t.Name)));
        texts.AddRange(p.ApprovalFlows.Select(f => ($"flow {f.Code}", f.Name)));
        texts.AddRange(p.ApprovalFlows.SelectMany(f => f.Stages!.Select(st => ($"flow {f.Code} stage {st.Code}", st.Name))));

        foreach (var (where, text) in texts)
            text.Values.Keys.Should().BeEquivalentTo(I18nText.SupportedLocales, where);
    }

    [Fact]
    public void FieldsTheCheckUses_AreFilterableInThePackage()
    {
        var move = Package.Value.Modules.Single(m => m.Code == "move");
        foreach (var code in new[] { "item", "warehouse", "qty" })
        {
            var field = move.Blocks.SelectMany(b => b.Fields).Single(f => f.Code == code);
            field.Config.GetProperty("isFilterable").GetBoolean().Should().BeTrue(code);
        }
    }

    [Fact]
    public void PoFlows_LargeThenMedium_DirectBelow()
    {
        var p = Package.Value;
        var po = p.Workflows.Single(w => w.Module == "po");
        var entry = po.Transitions.Where(t => t.Approval is not null).Should().ContainSingle().Subject;
        (entry.From, entry.To, entry.Approval!.Required, entry.Approval.NoMatch).Should().Be(("draft", "confirmed", true, "direct"), "小額直接確認");

        p.ApprovalFlows.Select(f => f.Code).Should().Equal("inv-po-large", "inv-po-medium");
        var large = p.ApprovalFlows[0];
        var medium = p.ApprovalFlows[1];
        foreach (var flow in p.ApprovalFlows)
        {
            (flow.Module, flow.To, flow.Shape, flow.Active).Should().Be(("po", "confirmed", "sequence", true));
            flow.FallbackApprovers!.Source.Should().Be("group", flow.Code);
            flow.FallbackApprovers.Groups.Should().Equal("manager");
        }
        (large.Priority, medium.Priority).Should().Be((1, 2), "大額優先");
        large.When!.Value.GetRawText().Should().Contain("\"po.total\"").And.Contain("\"gte\"").And.Contain("100000");
        medium.When!.Value.GetRawText().Should().Contain("\"po.total\"").And.Contain("\"gte\"").And.Contain("10000").And.NotContain("100000");

        large.Stages!.Select(s => $"{s.Code}:{s.Approvers.Source}:{s.Approvers.Level}:{s.DueHours}").Should().Equal("buyer-lead:manager:1:24", "gm:manager:2:48");
        medium.Stages!.Select(s => $"{s.Code}:{s.Approvers.Source}:{s.Approvers.Level}:{s.DueHours}").Should().Equal("buyer-lead:manager:1:24");
        p.ApprovalFlows.SelectMany(f => f.Stages!).Should().OnlyContain(s => s.Mode == "any" && s.OnEmpty == "fallback" && (s.EditableFields == null || s.EditableFields.Count == 0));
        // 手冊與 Demo 劇本照這幾個繁中名稱寫，改名要連手冊一起改
        large.Name.Resolve("zh-TW").Should().Be("採購單（大額）");
        medium.Name.Resolve("zh-TW").Should().Be("採購單（一萬以上）");
        large.Stages![0].Name.Resolve("zh-TW").Should().Be("採購主管");
        large.Stages[1].Name.Resolve("zh-TW").Should().Be("總經理");
    }

    [Fact]
    public void PoWorkflow_CancelledIsEditable_ClosedStaysReadOnly()
    {
        // 已取消可從草稿直接進入；不標可編輯會被判成「不簽就進鎖定狀態」（簽核設計 §20.4 第 6 條）；唯讀不變
        var po = Package.Value.Workflows.Single(w => w.Module == "po");
        var cancelled = po.States.Single(s => s.Option == "cancelled");
        (cancelled.Editable, cancelled.ReadOnly).Should().Be((true, true));
        po.States.Where(s => s.Option != "cancelled").Should().OnlyContain(s => s.Editable == null);
        po.States.Single(s => s.Option == "closed").ReadOnly.Should().BeTrue();
        AppPackageValidator.Validate(Package.Value).Errors.Should().BeEmpty();
    }
}
