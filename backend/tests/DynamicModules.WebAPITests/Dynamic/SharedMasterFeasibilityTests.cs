using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Acting;
using DynamicModules.Application.Dynamic.AppService.Commands;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 共用主檔設計（2026-11-10-shared-master-design.md）§15.1 的可行性驗證：主檔功能實作前，確認它依賴的既有能力成立。
/// 每個測試名稱前綴是 §15.1 的編號。用的是測試專用的虛構套件（AppTestPackages），不依賴內建應用程式。
/// </summary>
public class SharedMasterFeasibilityTests : IDisposable
{
    private const string Apps = Base + "/apps";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly TestApp _app = new(fileDatabase: true);
    private readonly HttpClient _admin;

    public SharedMasterFeasibilityTests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private async Task Import(string json) =>
        (await _admin.PostAsync($"{Apps}/packages/import", Body(json))).StatusCode.Should().Be(HttpStatusCode.Created);

    private static readonly object CreateCrmGroups = new
    {
        groupMap = new Dictionary<string, object> { ["sales"] = new { mode = "create" }, ["salesLead"] = new { mode = "create" } }
    };

    private async Task<JsonElement> OpenCrm()
    {
        await Import(AppTestPackages.Crm);
        var res = await _admin.PostAsJsonAsync($"{Apps}/packages/crm-lite/open", CreateCrmGroups);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private static string KeyOf(JsonElement app, string code) =>
        app.GetProperty("modules").EnumerateArray().Single(m => m.GetProperty("code").GetString() == code).GetProperty("key").GetString()!;

    private async Task<long> AddRecord(string moduleKey, Dictionary<string, object> values)
    {
        var res = await _admin.PostAsJsonAsync($"{Base}/modules/{moduleKey}/records", new { values });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    private T Db<T>(Func<DynamicDbContext, T> read)
    {
        using var scope = _app.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<DynamicDbContext>());
    }

    // ---- #1 同一個交易內先開一個，再開依賴它的；失敗整批回滾 ----

    [Fact]
    public async Task F1_TwoAppsOpenedInOneTransaction_TheSecondSeesTheFirst_AndRollbackUndoesBoth()
    {
        await Import(AppTestPackages.Crm);
        await Import(AppTestPackages.Quote);   // quote-lite requires crm-lite 的 customer

        foreach (var commit in new[] { false, true })
        {
            using var scope = _app.Services.CreateScope();
            using var acting = ActingScope.Begin("admin");
            var opener = scope.ServiceProvider.GetRequiredService<AppOpener>();
            await using var tx = await scope.ServiceProvider.GetRequiredService<ITransactionManager>().BeginAsync(default);

            var crm = await opener.OpenAsync("crm-lite", new OpenAppBody(null, null,
                new() { ["sales"] = new("create", null), ["salesLead"] = new("create", null) }, null), default);
            crm.Result.IsSuccess.Should().BeTrue(string.Join("；", crm.Result.Errors.SelectMany(e => e.Value)));
            // 第二個套件的相依檢查讀的是同一個交易裡、尚未提交的第一個應用程式
            var quote = await opener.OpenAsync("quote-lite", new OpenAppBody(null, null,
                new() { ["sales"] = new("skip", null) }, null), default);
            quote.Result.IsSuccess.Should().BeTrue(string.Join("；", quote.Result.Errors.SelectMany(e => e.Value)));

            if (commit) await tx.CommitAsync(default);
            else await tx.RollbackAsync(default);

            var keys = Db(db => db.Set<App>().Select(a => a.Key).OrderBy(k => k).ToList());
            if (commit) keys.Should().Equal("crm-lite", "quote-lite");
            else
            {
                keys.Should().BeEmpty("回滾後兩個應用程式都不存在");
                Db(db => db.Set<Module>().Count(m => m.Key == "crm-customer")).Should().Be(0, "實例化的模組也一起回滾");
            }
        }
    }

    [Fact]
    public async Task F1_TwoConcurrentOpensOfTheSamePackage_OneSucceeds_TheOtherSeesTheCommittedStateAndIs409()
    {
        // 檔案 SQLite：最外層交易先在 WriteGate 排隊，第二個請求的開啟計畫在第一個提交後才讀狀態，不會撞唯一索引變成 500
        await Import(AppTestPackages.Procure);
        var a = _app.AsUser();
        var b = _app.AsUser();
        var results = await Task.WhenAll(
            a.PostAsJsonAsync($"{Apps}/packages/procure-lite/open", new { }),
            b.PostAsJsonAsync($"{Apps}/packages/procure-lite/open", new { }));
        results.Select(r => r.StatusCode).Should().BeEquivalentTo([HttpStatusCode.Created, HttpStatusCode.Conflict]);
        Db(db => db.Set<App>().Count(x => x.Key == "procure-lite")).Should().Be(1);
    }

    // ---- #3 refGuard 的 in 可以列多個選項 ----

    [Fact]
    public async Task F3_RefGuardWithTwoAllowedOptions_AcceptsBoth_RejectsTheOther()
    {
        const string package = """
            {
              "format": "formwright-app", "formatVersion": 5,
              "manifest": { "id": "guard-two", "version": "1.0.0", "name": { "zh-TW": "守衛兩個選項" } },
              "modules": [
                { "code": "party", "key": "gt-party", "name": { "zh-TW": "對象" }, "kind": "standalone", "titleField": "name",
                  "blocks": [ { "code": "main", "label": { "zh-TW": "基本" }, "collapsed": false, "columns": 2, "fields": [
                    { "code": "name", "type": "text", "label": { "zh-TW": "name" }, "config": { "required": true } },
                    { "code": "role", "type": "enum", "label": { "zh-TW": "role" },
                      "options": [ { "code": "customer", "label": { "zh-TW": "客戶" }, "color": null },
                                   { "code": "supplier", "label": { "zh-TW": "供應商" }, "color": null },
                                   { "code": "both", "label": { "zh-TW": "兩者" }, "color": null } ],
                      "config": { "isFilterable": true } } ] } ] },
                { "code": "order", "key": "gt-order", "name": { "zh-TW": "訂單" }, "kind": "standalone", "titleField": "text",
                  "blocks": [ { "code": "main", "label": { "zh-TW": "基本" }, "collapsed": false, "columns": 2, "fields": [
                    { "code": "text", "type": "text", "label": { "zh-TW": "text" }, "config": { "required": true } },
                    { "code": "party", "type": "reference", "label": { "zh-TW": "party" },
                      "config": { "refTarget": { "kind": "module", "key": "party" },
                                  "refGuard": { "field": "party.role", "in": ["customer", "both"] } } } ] } ] }
              ]
            }
            """;
        await Import(package);
        (await _admin.PostAsJsonAsync($"{Apps}/packages/guard-two/open", new { })).StatusCode.Should().Be(HttpStatusCode.Created);

        var party = await FieldsOf("gt-party");
        var order = await FieldsOf("gt-order");
        var options = party["role"].GetProperty("options").EnumerateArray()
            .ToDictionary(o => o.GetProperty("label").GetProperty("zh-TW").GetString()!, o => o.GetProperty("value").GetString()!);
        async Task<long> Party(string role) => await AddRecord("gt-party",
            new() { [Key(party["name"])] = role, [Key(party["role"])] = options[role] });
        Task<HttpResponseMessage> Order(long partyId) => _admin.PostAsJsonAsync($"{Base}/modules/gt-order/records",
            new { values = new Dictionary<string, object> { [Key(order["text"])] = "o", [Key(order["party"])] = partyId } });

        (await Order(await Party("客戶"))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Order(await Party("兩者"))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Order(await Party("供應商"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<Dictionary<string, JsonElement>> FieldsOf(string moduleKey) =>
        (await JsonOf(await _admin.GetAsync($"{Base}/modules/{moduleKey}"))).GetProperty("blocks").EnumerateArray()
            .SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .ToDictionary(f => f.GetProperty("label").GetProperty("zh-TW").GetString()!);

    private static string Key(JsonElement field) => field.GetProperty("key").GetString()!;

    // ---- #6、#14：在另一個應用程式擁有、已啟用且有資料的模組上新增區塊與非必填欄位 ----

    [Fact]
    public async Task F6_DesignerAddsBlockAndOptionalFieldsToAnotherAppsActiveModuleWithData()
    {
        var crm = await OpenCrm();
        var customer = KeyOf(crm, "customer");
        var existing = await AddRecord(customer, new() { [await TitleKey(_admin, customer)] = "嵐岳精密" });

        var block = await _admin.PostAsJsonAsync($"{Base}/modules/{customer}/blocks",
            new { label = new Dictionary<string, string> { ["zh-TW"] = "進銷存" } });
        block.StatusCode.Should().Be(HttpStatusCode.Created, await block.Content.ReadAsStringAsync());
        var blockId = (await JsonOf(block)).GetProperty("id").GetInt64();

        foreach (var type in new[] { "text", "enum", "currency" })
        {
            var res = await _admin.PostAsJsonAsync($"{Base}/modules/{customer}/fields",
                new { blockId, type, label = new Dictionary<string, string> { ["zh-TW"] = type }, config = new { } });
            res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        }

        Db(db => db.Set<Module>().Single(m => m.Key == customer).Status).Should().Be(ModuleStatus.Active);
        (await _admin.GetAsync($"{Base}/records/{existing}")).StatusCode.Should().Be(HttpStatusCode.OK, "既有記錄照常可讀");
    }

    // ---- #4：轉換的目標是另一個應用程式的模組，追溯欄位是加在那個模組上的欄位 ----

    [Fact]
    public async Task F4_ConversionTargetingAnotherAppsModule_WithATraceFieldAddedThere_Converts()
    {
        var crm = await OpenCrm();
        var customer = KeyOf(crm, "customer");

        // 來源：不屬於 crm-lite 的模組（代表 CRM 自己的潛在客戶）；追溯欄位加在 crm-lite 的客戶上（代表 CRM 貢獻的 fromLead）
        var lead = await CreateModule(_admin);
        await Activate(_admin, lead);
        _app.GrantBusinessGroups(lead);
        var trace = await AddField(_admin, customer, "reference",
            new { refTarget = new { kind = "module", key = lead }, readOnly = true, onDelete = "setNull" });
        var leadTitle = await TitleKey(_admin, lead);
        var customerTitle = await TitleKey(_admin, customer);

        var key = "cv-" + Guid.NewGuid().ToString("N")[..8];
        var saved = await _admin.PostAsJsonAsync($"{Base}/conversions", new
        {
            key, name = new Dictionary<string, string> { ["zh-TW"] = "潛在客戶轉客戶" },
            sourceModuleKey = lead, targetModuleKey = customer, active = true,
            definition = new
            {
                sourceStates = Array.Empty<long>(),
                header = new { sourceFieldId = trace.Id, copy = new[] { new { from = long.Parse(leadTitle[2..]), to = long.Parse(customerTitle[2..]) } } }
            }
        });
        saved.StatusCode.Should().Be(HttpStatusCode.Created, await saved.Content.ReadAsStringAsync());

        var source = await AddRecord(lead, new() { [leadTitle] = "嵐岳精密" });
        var converted = await _admin.PostAsJsonAsync($"{Base}/records/{source}/conversions/{key}/convert", new { });
        converted.StatusCode.Should().Be(HttpStatusCode.Created, await converted.Content.ReadAsStringAsync());
        var values = (await JsonOf(converted)).GetProperty("values");
        values.GetProperty(customerTitle).GetString().Should().Be("嵐岳精密");
        values.GetProperty(trace.Key).GetInt64().Should().Be(source);
    }

    // ---- #5：權限列可在交易內讀取—合併—寫回，不經 SaveModulePermissions，其他種類的列不受影響 ----

    [Fact]
    public async Task F5_ModulePermissionRows_ReadMergeReplaceInsideATransaction_KeepsFieldAccessAndOptionScopes()
    {
        var crm = await OpenCrm();
        var customer = KeyOf(crm, "customer");
        using var scope = _app.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var module = (await sp.GetRequiredService<IModuleRepository>().GetByKeyAsync(customer, includeChildren: true, default))!;
        var permissions = sp.GetRequiredService<IPermissionRepository>();
        var before = await permissions.LoadModuleRowsAsync(module, default);
        before.FieldPermissions.Should().NotBeEmpty("crm-lite 有 fieldAccess");
        before.OptionScopes.Should().NotBeEmpty("crm-lite 有 optionScopes");
        var extraGroup = Db(db => db.Set<PermissionGroup>().First(g => !before.GroupPrivileges.Select(p => p.GroupId).Contains(g.Id)).Id);

        await using (var tx = await sp.GetRequiredService<ITransactionManager>().BeginAsync(default))
        {
            var rows = await permissions.LoadModuleRowsAsync(module, default);
            var merged = rows.GroupPrivileges.Select(Copy).ToList();
            merged.Add(new GroupProgramPrivilege { GroupId = extraGroup, ProgramCode = module.ProgramCode, CanRead = true });
            await permissions.ReplaceModuleRowsAsync(module, rows with
            {
                GroupPrivileges = merged,
                Scopes = [.. rows.Scopes.Select(s => new ModuleDataScope { ModuleId = s.ModuleId, GroupId = s.GroupId, Scope = s.Scope }),
                          new ModuleDataScope { ModuleId = module.Id, GroupId = extraGroup, Scope = DataScope.Own }],
                UserPrivileges = [], FieldPermissions = rows.FieldPermissions.Select(f => new FieldPermission { FieldId = f.FieldId, GroupId = f.GroupId, Access = f.Access }).ToList(),
                OptionScopes = rows.OptionScopes.Select(o => new FieldOptionScope { OptionId = o.OptionId, GroupId = o.GroupId }).ToList()
            }, default);
            await sp.GetRequiredService<IUnitOfWork>().CommitAsync(default);
            await tx.CommitAsync(default);
        }

        using var verify = _app.Services.CreateScope();
        var after = await verify.ServiceProvider.GetRequiredService<IPermissionRepository>().LoadModuleRowsAsync(module, default);
        after.GroupPrivileges.Select(p => p.GroupId).Should().BeEquivalentTo(before.GroupPrivileges.Select(p => p.GroupId).Append(extraGroup));
        after.Scopes.Single(s => s.GroupId == extraGroup).Scope.Should().Be(DataScope.Own);
        after.FieldPermissions.Select(f => (f.FieldId, f.GroupId, f.Access)).Should().BeEquivalentTo(before.FieldPermissions.Select(f => (f.FieldId, f.GroupId, f.Access)));
        after.OptionScopes.Select(o => (o.OptionId, o.GroupId)).Should().BeEquivalentTo(before.OptionScopes.Select(o => (o.OptionId, o.GroupId)));
    }

    private static GroupProgramPrivilege Copy(GroupProgramPrivilege p) => new()
    {
        GroupId = p.GroupId, ProgramCode = p.ProgramCode, CanRead = p.CanRead, CanAdd = p.CanAdd, CanEdit = p.CanEdit,
        CanDelete = p.CanDelete, CanPrint = p.CanPrint, CanAmt = p.CanAmt, CanApprove = p.CanApprove
    };

    // ---- #15：系統填入的固定選項值（轉換用的 systemKeys）不受唯讀限制，但仍經 RecordValueProcessor 驗證 ----

    [Fact]
    public async Task F15_FixedOptionValueWrittenAsSystemKey_IsStoredWhenValid_AndRejectedWhenNotAnOptionOfTheField()
    {
        var crm = await OpenCrm();
        var customer = KeyOf(crm, "customer");
        var role = await AddField(_admin, customer, "enum", new { readOnly = true });
        var other = await AddField(_admin, customer, "enum");
        async Task<string> Option(long fieldId, string label) => (await JsonOf(await _admin.PostAsJsonAsync($"{Base}/fields/{fieldId}/options",
            new { label = new Dictionary<string, string> { ["zh-TW"] = label } }))).GetProperty("value").GetString()!;
        var customerOption = await Option(role.Id, "客戶");
        var foreignOption = await Option(other.Id, "別的欄位的選項");
        var title = await TitleKey(_admin, customer);

        async Task<ServiceResult> Create(string option, bool system)
        {
            using var scope = _app.Services.CreateScope();
            using var acting = ActingScope.Begin("admin");
            var values = new Dictionary<string, JsonElement?>
            {
                [title] = JsonSerializer.SerializeToElement("嵐岳精密"), [role.Key] = JsonSerializer.SerializeToElement(option)
            };
            return await scope.ServiceProvider.GetRequiredService<MediatR.ISender>().Send(
                new Application.Dynamic.RecordService.Commands.CreateRecord.CreateRecordRequest(customer, values, null, null,
                    system ? new HashSet<string> { role.Key } : null), default);
        }

        var ok = await Create(customerOption, system: true);
        ok.IsCreated.Should().BeTrue(string.Join("；", ok.Errors.SelectMany(e => e.Value)));
        JsonSerializer.SerializeToElement(ok.Data, Web).GetProperty("values").GetProperty(role.Key).GetString().Should().Be(customerOption);

        (await Create(foreignOption, system: true)).Errors.Should().ContainKey(role.Key, "不是這個欄位的選項，照型別正規化擋下");

        var ignored = await Create(customerOption, system: false);
        ignored.IsCreated.Should().BeTrue();
        JsonSerializer.SerializeToElement(ignored.Data, Web).GetProperty("values").TryGetProperty(role.Key, out _).Should().BeFalse("一般輸入不能寫唯讀欄位");
    }

    // ---- #17：設計器新增欄位時，同一個交易內寫對照列；匯出時用對照列的持久代號 ----

    [Fact]
    public async Task F17_BindingWrittenInTheSameTransactionAsADesignerAddedField_GivesItAPersistentExportCode()
    {
        var crm = await OpenCrm();
        var customer = KeyOf(crm, "customer");
        using var scope = _app.Services.CreateScope();
        using var acting = ActingScope.Begin("admin");
        var sp = scope.ServiceProvider;
        var appId = Db(db => db.Set<App>().Single(a => a.Key == "crm-lite").Id);
        var blockId = Db(db => db.Set<Block>().First(b => b.ModuleId == db.Set<Module>().Single(m => m.Key == customer).Id).Id);

        await using (var tx = await sp.GetRequiredService<ITransactionManager>().BeginAsync(default))
        {
            var added = await sp.GetRequiredService<MediatR.ISender>().Send(new Application.Dynamic.FieldService.Commands.AddField.AddFieldRequest(
                customer, blockId, FieldType.Text, new I18nText(new Dictionary<string, string> { ["zh-TW"] = "等級" }), new()), default);
            added.IsCreated.Should().BeTrue(string.Join("；", added.Errors.SelectMany(e => e.Value)));
            var fieldId = ((Application.Dynamic.ModuleService.Data.FieldResponse)added.Data!).Id;
            sp.GetRequiredService<IAppRepository>().AddBindings([new AppField { AppId = appId, Name = "customer.vipLevel", FieldId = fieldId }], [], []);
            await sp.GetRequiredService<IUnitOfWork>().CommitAsync(default);
            await tx.CommitAsync(default);
        }

        var export = await _admin.GetAsync($"{Apps}/crm-lite/export");
        export.StatusCode.Should().Be(HttpStatusCode.OK);
        var fields = (await JsonOf(export)).GetProperty("modules").EnumerateArray().Single(m => m.GetProperty("code").GetString() == "customer")
            .GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .Select(f => f.GetProperty("code").GetString()).ToList();
        fields.Should().Contain("vipLevel");
    }
}
