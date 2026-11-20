using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateField;
using DynamicModules.Application.Dynamic.FieldService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Policies;

/// <summary>#66：靜態路徑的訊息名稱用請求語系（預設 zh-TW、請求 en-US），匯出的資料不隨請求語系變。</summary>
public class RequestLocaleStaticPathTests : EffectsTestBase
{
    private sealed class English : ICurrentLocale { public string Locale => "en-US"; }

    protected override void ConfigureServices(IServiceCollection services) => services.AddScoped<ICurrentLocale, English>();

    private static I18nText Two(string zh, string en) => new(new Dictionary<string, string> { ["zh-TW"] = zh, ["en-US"] = en });

    private async Task<FieldResponse> Add(string module, FieldType type, string zh, string en, FieldConfig? config = null)
    {
        var block = (await Detail(module)).Blocks[0].Id;
        var r = await Send(new AddFieldRequest(module, block, type, Two(zh, en), config ?? new FieldConfig()));
        r.IsCreated.Should().BeTrue(string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (FieldResponse)r.Data!;
    }

    private async Task<(FieldConfig Fill, long RefId, long BlockId)> FillSetup()
    {
        await NewModule("rfs-src");
        var src = await Add("rfs-src", FieldType.Text, "條件", "Terms");
        await NewModule("rfs-dst");
        var open = await Add("rfs-dst", FieldType.Text, "可編輯", "Editable");
        var block = (await Detail("rfs-dst")).Blocks[0].Id;
        var fill = RefTo("rfs-src") with { Fill = new FillConfig(FillConfig.Lock, [new FillMapping(src.Id, open.Id)]) };
        return (fill, 0, block);
    }

    [Fact]
    public async Task AddField_FillLockMessage_UsesRequestLocale()
    {
        var (fill, _, block) = await FillSetup();
        var r = await Send(new AddFieldRequest("rfs-dst", block, FieldType.Reference, Text("來源"), fill));
        r.IsCreated.Should().BeFalse();
        r.Errors["Config.Fill"].Should().Contain(m => m.Contains("Editable") && !m.Contains("可編輯"));
    }

    [Fact]
    public async Task UpdateField_FillLockMessage_UsesRequestLocale()
    {
        var (fill, _, block) = await FillSetup();
        var reference = await Add("rfs-dst", FieldType.Reference, "來源", "Source", RefTo("rfs-src"));
        var r = await Send(new UpdateFieldRequest(reference.Id, FieldType.Reference, Text("來源"), fill, block));
        r.IsSuccess.Should().BeFalse();
        r.Errors["Config.Fill"].Should().Contain(m => m.Contains("Editable") && !m.Contains("可編輯"));
    }

    private sealed class Fixed(string locale) : ICurrentLocale { public string Locale => locale; }

    [Fact]
    public async Task TemplateExport_DataSameAcrossRequestLocales()
    {
        await NewModule("rfs-tpl");
        await Add("rfs-tpl", FieldType.Text, "品項", "Item");
        var module = (await Svc<IModuleRepository>().GetByKeyAsync("rfs-tpl", includeChildren: true, default))!;

        async Task<string> Export(string locale)
        {
            var exporter = new TemplateExporter(Svc<IModuleRepository>(), Svc<IPermissionRepository>(), Svc<IListViewRepository>(),
                new RequestLocale(new Fixed(locale), Svc<CachedSystemPolicy>()));
            var package = await exporter.ExportAsync(module, default);
            return JsonSerializer.Serialize(package with { Warnings = null }, TemplateJson.Options);
        }

        var zh = await Export("zh-TW");
        var en = await Export("en-US");
        en.Should().Be(zh);
        zh.Should().Contain("品項").And.Contain("Item");
    }
}
