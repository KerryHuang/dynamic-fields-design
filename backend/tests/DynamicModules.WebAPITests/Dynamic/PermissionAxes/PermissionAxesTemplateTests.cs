using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Domain.Authorization;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using static DynamicModules.WebAPITests.Dynamic.PermissionAxes.PaxWorld;

namespace DynamicModules.WebAPITests.Dynamic.PermissionAxes;

/// <summary>
/// 從範本建立沒有權限設定時略過權限建議（M3，PAX-11；權限軸設計 §7.3）。測試範本 t-perm：一個模組、群組權限建議指向 G-design、
/// 一個帶預設值的欄位（第 5 步會送 UpdateField，要設計器修改）。
/// </summary>
public sealed class PermissionAxesTemplateTests : IAsyncLifetime
{
    internal const string TPerm = $$"""
        { "formatVersion": 1, "key": "t-perm", "name": { "zh-TW": "權限範本" },
          "modules": [ { "code": "p", "key": "pax-tp", "name": { "zh-TW": "範本模組" }, "kind": "standalone", "titleField": "title",
            "blocks": [ { "code": "main", "label": { "zh-TW": "主要" }, "fields": [
              { "code": "title", "type": "text", "label": { "zh-TW": "標題" } },
              { "code": "qty", "type": "decimal", "label": { "zh-TW": "數量" }, "config": { "defaultValue": 1 } } ] } ],
            "permissions": [
              { "group": "{{GDesignKey}}", "scope": "all", "privileges": { "read": true, "add": true, "edit": false, "delete": false,
                "printExport": false, "pricing": false, "approve": false } } ] } ] }
        """;

    /// <summary>TemplateInstantiator.PermissionsSkippedWarning（internal）的原文。</summary>
    internal const string SkippedWarning = "你沒有「權限設定」權限，已略過範本的權限建議；請洽權限設定者到權限分頁授權";

    private sealed class App : TestApp
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(s =>
            {
                var packages = TemplateCatalog.Load(TemplateCatalog.DefaultDirectory).BuiltIns.ToList();
                packages.Add(TemplateJson.Parse(TPerm).Package!);
                s.RemoveAll<TemplateCatalog>();
                s.AddSingleton(new TemplateCatalog(packages));
            });
        }
    }

    private readonly App _app = new();
    private PaxWorld _w = null!;

    public async Task InitializeAsync() => _w = await CreateAsync(_app, modules: false);

    public Task DisposeAsync()
    {
        _app.Dispose();
        return Task.CompletedTask;
    }

    private Task<HttpResponseMessage> Instantiate(string user, string key) => _w.As(user).PostAsJsonAsync($"{Base}/templates/t-perm/instantiate",
        new { modules = new[] { new { code = "p", key } }, excludeFields = Array.Empty<string>() });

    private string? ProgramOf(string key) => _w.Db(db => db.Set<Module>().Where(m => m.Key == key).Select(m => m.ProgramCode).SingleOrDefault());

    private List<long> GroupsWithRows(string key)
    {
        var code = ProgramOf(key)!;
        return _w.Db(db => db.Set<GroupProgramPrivilege>().Where(p => p.ProgramCode == code).Select(p => p.GroupId).ToList());
    }

    [Fact]
    public async Task PAX11_DesignerWithoutPermissionAdmin_CreatesButSkipsPermissions_AddOnly403()
    {
        var wang = await Instantiate(Wang, "pax-tp-wang");
        wang.StatusCode.Should().Be(HttpStatusCode.Created, await wang.Content.ReadAsStringAsync());
        (await JsonOf(wang)).GetProperty("warnings").EnumerateArray().Select(w => w.GetString())
            .Should().Contain(SkippedWarning);
        GroupsWithRows("pax-tp-wang").Should().BeEmpty("新模組預設沒人有權限");
        var audit = _w.Db(db => db.Set<AuditLogEntry>().AsNoTracking().Where(a => a.Action == "template.instantiate").OrderByDescending(a => a.Id).First());
        audit.ActorUserId.Should().Be(Wang);
        JsonDocument.Parse(audit.SummaryJson!).RootElement.GetProperty("permissionsSkipped").GetBoolean().Should().BeTrue();

        var chen = await Instantiate(Chen, "pax-tp-chen");
        chen.StatusCode.Should().Be(HttpStatusCode.Created, await chen.Content.ReadAsStringAsync());
        (await JsonOf(chen)).GetProperty("warnings").EnumerateArray().Select(w => w.GetString())
            .Should().NotContain(SkippedWarning);
        GroupsWithRows("pax-tp-chen").Should().Equal(_w.GDesign);
        var chenAudit = _w.Db(db => db.Set<AuditLogEntry>().AsNoTracking().Where(a => a.Action == "template.instantiate").OrderByDescending(a => a.Id).First());
        (chenAudit.SummaryJson ?? "").Should().NotContain("permissionsSkipped");

        // 只有設計器讀取＋新增：巢狀 UpdateField、UpdateModule 要修改 → 403，整批回滾
        _w.Write(db =>
        {
            db.Set<AppUser>().Add(new AppUser { UserId = "adder", DisplayName = "只會新增" });
            db.Set<UserProgramPrivilege>().Add(new UserProgramPrivilege { UserId = "adder", ProgramCode = SystemProgramCodes.Designer, CanRead = true, CanAdd = true });
        });
        var adder = await Instantiate("adder", "pax-tp-adder");
        adder.StatusCode.Should().Be(HttpStatusCode.Forbidden, await adder.Content.ReadAsStringAsync());
        ProgramOf("pax-tp-adder").Should().BeNull("整批回滾");
    }
}
