using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.AccountService.Commands.CreateGroup;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddBlock;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateField;
using DynamicModules.Application.Dynamic.FieldService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.UpdateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.OrganizationService;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Policies;

/// <summary>必填語系政策 locale.requiredText（政策設計 §3.5、p3 計畫 Task 9）：出廠訊息不變、新增照政策擋、修改只擋改了的文字。</summary>
public class RequiredTextPolicyTests : DesignerTestBase
{
    private async Task SetRequired(params string[] locales) =>
        (await Send(new UpdateSystemPolicyRequest(PolicyCatalog.LocaleRequiredText.Code, JsonSerializer.SerializeToElement(locales))))
            .IsSuccess.Should().BeTrue();

    private static I18nText T(params (string Locale, string Text)[] items) => new(items.ToDictionary(i => i.Locale, i => i.Text));

    [Fact]
    public async Task Factory_MessagesUnchanged()
    {
        await NewModule("memo");
        var field = await AddField("memo", FieldType.Text, label: " ");
        Err(field, "Label").Should().Be("名稱必須有繁體中文");
        var english = await Send(new AddBlockRequest("memo", T(("en-US", "Block"))));
        Err(english, "Label").Should().Be("名稱必須有繁體中文");
        var module = await Send(new CreateModuleRequest("other", T(("en-US", "Other")), null, null, ModuleKind.Standalone, null));
        Err(module, "Name").Should().Be("名稱必須有繁體中文");
        var dept = await Send(new CreateDepartmentRequest(new DepartmentInput("d1", T(("en-US", "Sales")), null, null, 0, true)));
        Err(dept, "name").Should().Be("名稱必須有繁體中文");
    }

    [Fact]
    public async Task AddField_RequiresEveryRequiredLocale()
    {
        await NewModule("memo");
        await SetRequired("zh-TW", "en-US");
        var r = await AddField("memo", FieldType.Text, label: "備註");
        r.IsInvalid.Should().BeTrue();
        Err(r, "Label").Should().Be("名稱必須有英文");
        var m = await Detail("memo");
        (await Send(new Application.Dynamic.FieldService.Commands.AddField.AddFieldRequest(
            "memo", m.Blocks[0].Id, FieldType.Text, T(("zh-TW", "備註"), ("en-US", "Note")), new FieldConfig()))).IsCreated.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task UpdateField_UnchangedLabel_SkipsNewRequiredLocale()
    {
        await NewModule("memo");
        var f = await MustAdd("memo", FieldType.Text, label: "備註");
        await SetRequired("zh-TW", "en-US");
        // 同樣的標籤（多一點空白也算沒改），只改「必填」
        var r = await Send(new UpdateFieldRequest(f.Id, f.Type, T(("zh-TW", " 備註 ")), new FieldConfig { Required = true }, f.BlockId));
        r.IsSuccess.Should().BeTrue(string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task UpdateField_ChangedLabel_RequiresNewLocale()
    {
        await NewModule("memo");
        var f = await MustAdd("memo", FieldType.Text, label: "備註");
        await SetRequired("zh-TW", "en-US");
        var bad = await Send(new UpdateFieldRequest(f.Id, f.Type, T(("zh-TW", "說明")), new FieldConfig(), f.BlockId));
        bad.IsInvalid.Should().BeTrue();
        Err(bad, "Label").Should().Be("名稱必須有英文");
        (await Send(new UpdateFieldRequest(f.Id, f.Type, T(("zh-TW", "說明"), ("en-US", "Note")), new FieldConfig(), f.BlockId)))
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateModule_UnchangedName_SkipsNewRequiredLocale()
    {
        await NewModule("memo");
        var m = await Detail("memo");
        await SetRequired("zh-TW", "en-US");
        (await Send(new UpdateModuleRequest("memo", Text("memo"), "star", null, m.TitleFieldId))).IsSuccess.Should().BeTrue();
        var renamed = await Send(new UpdateModuleRequest("memo", Text("備忘"), null, null, m.TitleFieldId));
        Err(renamed, "Name").Should().Be("名稱必須有英文");
    }

    [Fact]
    public async Task EnglishOnly_NewFieldWithoutZhTw_ResolvesToEnglish()
    {
        await NewModule("memo");
        await SetRequired("en-US");
        var m = await Detail("memo");
        var r = await Send(new Application.Dynamic.FieldService.Commands.AddField.AddFieldRequest(
            "memo", m.Blocks[0].Id, FieldType.Text, T(("en-US", "Note")), new FieldConfig()));
        r.IsCreated.Should().BeTrue(string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        var label = (await Db.Context.Set<Field>().AsNoTracking().SingleAsync(x => x.Id == ((FieldResponse)r.Data!).Id)).LabelI18n;
        label.Resolve("th").Should().Be("Note");
    }

    [Fact]
    public async Task CreateGroup_WritesNameIntoEveryRequiredLocale()
    {
        await SetRequired("zh-TW", "en-US");
        var r = await Send(new CreateGroupRequest("Kho", "vi"));
        r.IsCreated.Should().BeTrue(r.Message);
        var name = (await Db.Context.Set<PermissionGroup>().AsNoTracking().ToListAsync()).Single(g => g.NameI18n.Values.ContainsKey("vi")).NameI18n;
        name.Values.Should().BeEquivalentTo(new Dictionary<string, string> { ["vi"] = "Kho", ["zh-TW"] = "Kho", ["en-US"] = "Kho" });
    }
}
