using System.Text.Json;
using DynamicModules.Application.Dynamic.CheckService;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.FieldService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Policies;

/// <summary>
/// 訊息內插的標籤跟請求語系與退回鏈走（p3 計畫 Task 11a、11b）。Application 測試沒有 HTTP 請求，請求語系＝預設語系（政策 locale.default）。
/// </summary>
public class LocaleLabelTests : EffectsTestBase
{
    private async Task DefaultLocale(string locale) =>
        (await Send(new UpdateSystemPolicyRequest(PolicyCatalog.LocaleDefault.Code, JsonSerializer.SerializeToElement(locale))))
            .IsSuccess.Should().BeTrue();

    private async Task<FieldResponse> AddTwo(string module, FieldType type, string zh, string en)
    {
        var block = (await Detail(module)).Blocks[0].Id;
        var r = await Send(new AddFieldRequest(module, block, type,
            new I18nText(new Dictionary<string, string> { ["zh-TW"] = zh, ["en-US"] = en }), new FieldConfig()));
        r.IsCreated.Should().BeTrue(string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (FieldResponse)r.Data!;
    }

    private async Task<IReadOnlyList<string>> CheckNotices(string module)
    {
        await NewModule(module);
        var item = await AddTwo(module, FieldType.Text, "品項", "Item");
        var qty = await AddTwo(module, FieldType.Decimal, "數量", "Quantity");
        await Send(new ActivateModuleRequest(module));
        var def = new CheckDefinition([item.Id], "sum", qty.Id, null, new CheckAssert("lte", new CheckLimit(10, null, null)), Text("太多"));
        var saved = await Send(new CreateCheckRequest(new CheckInput(module + "-cap", Text("cap"), module, true, def)));
        saved.IsCreated.Should().BeTrue(string.Join(";", saved.Errors.Select(e => e.Key + e.Value[0])));
        return ((CheckResponse)saved.Data!).Notices;
    }

    [Fact]
    public async Task CheckIndexNotice_UsesDefaultLocale()
    {
        await DefaultLocale("en-US");
        (await CheckNotices("lle")).Should().Contain(n => n.Contains("「Item」"));
    }

    [Fact]
    public async Task FillLockTargetNotReadOnly_UsesDefaultLocale()
    {
        await DefaultLocale("en-US");
        await NewModule("llf-src");
        var src = await AddTwo("llf-src", FieldType.Text, "條件", "Terms");
        await NewModule("llf-dst");
        var open = await AddTwo("llf-dst", FieldType.Text, "可編輯", "Editable");
        var block = (await Detail("llf-dst")).Blocks[0].Id;
        var r = await Send(new AddFieldRequest("llf-dst", block, FieldType.Reference, Text("來源"), RefTo("llf-src") with
        {
            Fill = new FillConfig(FillConfig.Lock, [new FillMapping(src.Id, open.Id)])
        }));
        r.IsCreated.Should().BeFalse();
        r.Errors["Config.Fill"].Should().Contain(m => m.Contains("Editable") && !m.Contains("可編輯"));
    }

    [Fact]
    public async Task CheckIndexNotice_Factory_Unchanged()
    {
        (await CheckNotices("llz")).Should().Contain("已自動啟用欄位「品項」的可篩選");
    }
}

/// <summary>
/// 寫進資料庫、稽核或記錄檔的文字用系統預設語系，不跟請求語系（N1，使用者 2026-11-20 裁決）：預設英文、介面越南文時，
/// 簽核歷程的備援說明與記錄標題（標題樣板的選項標籤）都存英文。
/// </summary>
public class SystemLocaleTextTests : EffectsTestBase
{
    private sealed class Vietnamese : ICurrentLocale { public string Locale => "vi"; }

    protected override void ConfigureServices(IServiceCollection services) => services.AddScoped<ICurrentLocale, Vietnamese>();

    private static I18nText Two(string zh, string en) => new(new Dictionary<string, string> { ["zh-TW"] = zh, ["en-US"] = en });

    private async Task EnglishDefault() =>
        (await Send(new UpdateSystemPolicyRequest(PolicyCatalog.LocaleDefault.Code, JsonSerializer.SerializeToElement("en-US"))))
            .IsSuccess.Should().BeTrue();

    [Fact]
    public async Task ApprovalFallbackNote_StoredInDefaultLocale_NotUiLocale()
    {
        await EnglishDefault();
        var group = new PermissionGroup { Key = "g-empty", NameI18n = Two("業務", "Sales") };
        Db.Context.Set<PermissionGroup>().Add(group);
        await Db.Context.SaveChangesAsync();
        using var scope = NewScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ApproverResolver>();
        var ctx = await resolver.ContextAsync(new Module(), "admin", new Dictionary<string, JsonElement?>(), null, default);
        // 組織快照只放兩個啟用中的人：發起人 admin、備援 boss（群組本身沒有成員）
        ctx = ctx with
        {
            Org = new Domain.Organization.OrgSnapshot(new Dictionary<long, Domain.Organization.OrgDepartment>(), new Dictionary<string, long>(),
                new[] { "admin", "boss" }.ToHashSet()),
            DefaultFallback = ["boss"]
        };
        var r = ApproverResolver.Resolve(ctx, Two("審核", "Review"), ApprovalModes.Any,
            new ApproverSource(ApprovalSources.Group, GroupIds: [group.Id]), null, ApprovalOnEmpty.Fallback);
        r.Error.Should().BeNull(r.Error?.Resolve("zh-TW"));
        r.Seats.Should().Contain(s => s.FallbackFor != null && s.FallbackFor.Contains("Sales"));
    }

    [Fact]
    public async Task TitleTemplateOption_StoredInDefaultLocale_NotUiLocale()
    {
        await EnglishDefault();
        var module = new Module
        {
            TitleTemplate = "{7}",
            Blocks = [new Block { Fields = [new Field { Id = 7, Key = "f_7", Type = FieldType.Enum,
                Options = [new FieldOption { Id = 1, LabelI18n = Two("高", "High") }] }] }]
        };
        using var scope = NewScope();
        var lc = scope.ServiceProvider.GetRequiredService<RequestLocale>();
        lc.Current.Locale.Should().Be("vi");
        Application.Dynamic.RecordService.RecordValueProcessor.TitleOf(module,
            new Dictionary<string, JsonElement?> { ["f_7"] = JsonSerializer.SerializeToElement("opt_1") }, lc.SystemDefault).Should().Be("High");
    }
}
