using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 模組 Closed 時設計器的定義寫入：設計文件 §6.3 明文「權限設定仍可修改；欄位、規則的編輯與 Inactive 模組相同，不另設限制」，
/// 所以預設放行。唯一的例外是會連帶改寫既有記錄值的變更（換標題欄位、公式與彙總重算、刪欄位或區塊）：
/// 它們違反「資料唯讀」，系統管理員也一律擋下。
/// </summary>
public class ClosedDefinitionTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _admin;

    public ClosedDefinitionTests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    private void SetStatus(string moduleKey, ModuleStatus status)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        db.Set<Module>().Single(m => m.Key == moduleKey).Status = status;
        db.SaveChanges();
    }

    private sealed record Sample(
        string Key, string TitleKey, long TitleFieldId, (long Id, string Key, long BlockId) Name2, (long Id, string Key, long BlockId) Qty,
        (long Id, string Key, long BlockId) Total, (long Id, string Key, long BlockId) Status, long RecordId);

    /// <summary>有第二個文字欄位、數量、公式（數量×3）與下拉欄位的啟用中模組，已有一筆記錄；回傳時尚未關閉。</summary>
    private async Task<Sample> MakeSample()
    {
        var key = await CreateModule(_admin);
        var name2 = await AddField(_admin, key, "text");
        var qty = await AddField(_admin, key, "integer");
        var total = await AddField(_admin, key, "formula", new { formula = $"{{{qty.Id}}}*3", resultType = "number" });
        var status = await AddField(_admin, key, "enum");
        await Activate(_admin, key);
        var title = await TitleKey(_admin, key);
        var module = await JsonOf(await _admin.GetAsync($"{Base}/modules/{key}"));
        var create = await _admin.PostAsJsonAsync($"{Base}/modules/{key}/records",
            new { values = new Dictionary<string, object> { [title] = "原標題", [name2.Key] = "新標題候選", [qty.Key] = 4 } });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var record = await JsonOf(create);
        return new Sample(key, title, module.GetProperty("titleFieldId").GetInt64(), name2, qty, total, status, record.GetProperty("id").GetInt64());
    }

    private async Task<JsonElement> RecordOf(long id) => await JsonOf(await _admin.GetAsync($"{Base}/records/{id}"));

    private Task<HttpResponseMessage> PutModule(Sample s, long titleFieldId, string name = "測試模組") =>
        _admin.PutAsJsonAsync($"{Base}/modules/{s.Key}", new
        {
            name = new Dictionary<string, string> { ["zh-TW"] = name }, icon = (string?)null, section = (string?)null, titleFieldId,
        });

    private Task<HttpResponseMessage> PutField(long id, string type, long blockId, object config, string label = "欄位") =>
        _admin.PutAsJsonAsync($"{Base}/fields/{id}", new { type, label = new Dictionary<string, string> { ["zh-TW"] = label }, config, blockId });

    private static async Task ShouldBeConflict(HttpResponseMessage res)
    {
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await res.Content.ReadAsStringAsync()).Should().Contain("應用程式已關閉");
    }

    // ── 換標題欄位 ──

    [Fact]
    public async Task Closed_ChangeTitleField_IsRejected_AndRecordTitlesStayTheSame()
    {
        var s = await MakeSample();
        SetStatus(s.Key, ModuleStatus.Closed);

        var res = await PutModule(s, s.Name2.Id);

        await ShouldBeConflict(res);
        (await RecordOf(s.RecordId)).GetProperty("title").GetString().Should().Be("原標題");
        (await JsonOf(await _admin.GetAsync($"{Base}/modules/{s.Key}"))).GetProperty("titleFieldId").GetInt64().Should().Be(s.TitleFieldId);
    }

    [Fact]
    public async Task Closed_RenameModule_KeepingTheTitleField_Succeeds()
    {
        var s = await MakeSample();
        SetStatus(s.Key, ModuleStatus.Closed);

        var res = await PutModule(s, s.TitleFieldId, "改過的名稱");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(await _admin.GetAsync($"{Base}/modules/{s.Key}"))).GetProperty("name").GetProperty("zh-TW").GetString().Should().Be("改過的名稱");
        (await RecordOf(s.RecordId)).GetProperty("title").GetString().Should().Be("原標題");
    }

    [Fact]
    public async Task Active_ChangeTitleField_StillRewritesTitles()
    {
        var s = await MakeSample();

        (await PutModule(s, s.Name2.Id)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await RecordOf(s.RecordId)).GetProperty("title").GetString().Should().Be("新標題候選");
    }

    // ── 規格明文放行的定義編輯 ──

    [Fact]
    public async Task Closed_BlockFieldOptionRulePermissionEdits_FollowTheSpec_AndAreAllowed()
    {
        var s = await MakeSample();
        SetStatus(s.Key, ModuleStatus.Closed);
        var label = new Dictionary<string, string> { ["zh-TW"] = "新" };

        var addBlock = await _admin.PostAsJsonAsync($"{Base}/modules/{s.Key}/blocks", new { label });
        addBlock.StatusCode.Should().Be(HttpStatusCode.Created);
        var blockId = (await JsonOf(addBlock)).GetProperty("id").GetInt64();
        (await _admin.PutAsJsonAsync($"{Base}/blocks/{blockId}", new { label, collapsed = true, columns = 2 })).StatusCode.Should().Be(HttpStatusCode.OK);

        var addField = await _admin.PostAsJsonAsync($"{Base}/modules/{s.Key}/fields", new { blockId, type = "text", label, config = new { } });
        addField.StatusCode.Should().Be(HttpStatusCode.Created);
        var newField = (await JsonOf(addField)).GetProperty("id").GetInt64();
        (await PutField(newField, "text", blockId, new { }, "改標籤")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _admin.PutAsJsonAsync($"{Base}/fields/{newField}/status", new { status = "Inactive" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _admin.PutAsJsonAsync($"{Base}/fields/{newField}/status", new { status = "Active" })).StatusCode.Should().Be(HttpStatusCode.OK);

        var addOption = await _admin.PostAsJsonAsync($"{Base}/fields/{s.Status.Id}/options", new { label, color = (string?)null });
        addOption.StatusCode.Should().Be(HttpStatusCode.Created);
        var optionId = (await JsonOf(addOption)).GetProperty("id").GetInt64();
        (await _admin.PutAsJsonAsync($"{Base}/options/{optionId}", new { label, color = (string?)null, active = false })).StatusCode.Should().Be(HttpStatusCode.OK);

        var rules = await JsonOf(await _admin.GetAsync($"{Base}/modules/{s.Key}/rules"));
        (await _admin.PutAsJsonAsync($"{Base}/modules/{s.Key}/rules", new { rules = rules.ValueKind == JsonValueKind.Array ? rules : rules.GetProperty("rules") })).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var perms = await JsonOf(await _admin.GetAsync($"{Base}/modules/{s.Key}/permissions"));
        (await _admin.PutAsJsonAsync($"{Base}/modules/{s.Key}/permissions", perms)).StatusCode.Should().Be(HttpStatusCode.OK);

        // 沒有任何一項動到既有記錄
        (await RecordOf(s.RecordId)).GetProperty("title").GetString().Should().Be("原標題");
    }

    // ── 公式與彙總重算會改寫記錄值 ──

    [Fact]
    public async Task Closed_ChangeFormula_IsRejected_AndStoredValuesAndRowVersionStayTheSame()
    {
        var s = await MakeSample();
        var before = await RecordOf(s.RecordId);
        SetStatus(s.Key, ModuleStatus.Closed);

        var res = await PutField(s.Total.Id, "formula", s.Total.BlockId, new { formula = $"{{{s.Qty.Id}}}*5", resultType = "number" });

        await ShouldBeConflict(res);
        var after = await RecordOf(s.RecordId);
        after.GetProperty("values").GetProperty(s.Total.Key).GetDecimal().Should().Be(12m);
        after.GetProperty("rowVersion").GetString().Should().Be(before.GetProperty("rowVersion").GetString());
    }

    [Fact]
    public async Task Closed_EditFormulaLabelOnly_IsAllowed_BecauseNothingIsRecalculated()
    {
        var s = await MakeSample();
        SetStatus(s.Key, ModuleStatus.Closed);

        var res = await PutField(s.Total.Id, "formula", s.Total.BlockId, new { formula = $"{{{s.Qty.Id}}}*3", resultType = "number" }, "只改標籤");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Closed_AddFormulaField_IsRejected_AndNoFieldIsLeftBehind()
    {
        var s = await MakeSample();
        SetStatus(s.Key, ModuleStatus.Closed);
        var fieldCount = async () => (await JsonOf(await _admin.GetAsync($"{Base}/modules/{s.Key}")))
            .GetProperty("blocks").EnumerateArray().Sum(b => b.GetProperty("fields").GetArrayLength());
        var before = await fieldCount();

        var res = await _admin.PostAsJsonAsync($"{Base}/modules/{s.Key}/fields", new
        {
            blockId = s.Total.BlockId, type = "formula", label = new Dictionary<string, string> { ["zh-TW"] = "新公式" },
            config = new { formula = $"{{{s.Qty.Id}}}*2", resultType = "number" },
        });

        await ShouldBeConflict(res);
        (await fieldCount()).Should().Be(before);
    }

    [Fact]
    public async Task Closed_ReactivateFormulaField_IsRejected_AndItStaysInactive()
    {
        var s = await MakeSample();
        (await _admin.PutAsJsonAsync($"{Base}/fields/{s.Total.Id}/status", new { status = "Inactive" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var before = await RecordOf(s.RecordId);
        SetStatus(s.Key, ModuleStatus.Closed);

        var res = await _admin.PutAsJsonAsync($"{Base}/fields/{s.Total.Id}/status", new { status = "Active" });

        await ShouldBeConflict(res);
        (await RecordOf(s.RecordId)).GetProperty("rowVersion").GetString().Should().Be(before.GetProperty("rowVersion").GetString());
        var module = await JsonOf(await _admin.GetAsync($"{Base}/modules/{s.Key}"));
        module.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .Single(f => f.GetProperty("id").GetInt64() == s.Total.Id).GetProperty("status").GetString().Should().Be("Inactive");
    }

    /// <summary>父模組（有彙總欄位）與子模組（明細），回傳父、子與子模組的數量欄位。</summary>
    private async Task<(string Parent, string Child, (long Id, string Key, long BlockId) Qty, long ParentRecord, long ChildRecord)> MakeRollupPair()
    {
        var parent = await CreateModule(_admin);
        var child = await CreateModule(_admin);
        var parentRef = await AddField(_admin, child, "reference", new { refTarget = new { kind = "module", key = parent } });
        var qty = await AddField(_admin, child, "decimal", new { scale = 0 });
        await AddField(_admin, parent, "rollup", new { rollup = new { sourceFieldId = parentRef.Id, function = "sum", aggregateFieldId = qty.Id } });
        await Activate(_admin, parent);
        await Activate(_admin, child);
        var parentTitle = await TitleKey(_admin, parent);
        var pr = await JsonOf(await _admin.PostAsJsonAsync($"{Base}/modules/{parent}/records",
            new { values = new Dictionary<string, object> { [parentTitle] = "父" } }));
        var childTitle = await TitleKey(_admin, child);
        var cr = await JsonOf(await _admin.PostAsJsonAsync($"{Base}/modules/{child}/records",
            new { values = new Dictionary<string, object> { [childTitle] = "子", [parentRef.Key] = pr.GetProperty("id").GetInt64(), [qty.Key] = 7 } }));
        return (parent, child, qty, pr.GetProperty("id").GetInt64(), cr.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task ActiveChild_ChangeSourceFieldScale_IsRejected_WhenItWouldRewriteAClosedParentsRollup()
    {
        var p = await MakeRollupPair();
        var before = await RecordOf(p.ParentRecord);
        SetStatus(p.Parent, ModuleStatus.Closed);

        // 改小數位會讓父模組的彙總重新推導 Scale，並重算父記錄
        var res = await PutField(p.Qty.Id, "decimal", p.Qty.BlockId, new { scale = 2 });

        await ShouldBeConflict(res);
        (await RecordOf(p.ParentRecord)).GetProperty("rowVersion").GetString().Should().Be(before.GetProperty("rowVersion").GetString());
    }

    [Fact]
    public async Task ActiveChild_ChangeItsFormula_IsRejected_WhenTheRecalcWouldReachAClosedParentsRollup()
    {
        var p = await MakeRollupPair();
        var formula = await AddField(_admin, p.Child, "formula", new { formula = $"{{{p.Qty.Id}}}*2", resultType = "number", scale = 0 });
        var before = await RecordOf(p.ParentRecord);
        SetStatus(p.Parent, ModuleStatus.Closed);

        var res = await PutField(formula.Id, "formula", formula.BlockId, new { formula = $"{{{p.Qty.Id}}}*9", resultType = "number", scale = 0 });

        await ShouldBeConflict(res);
        (await RecordOf(p.ParentRecord)).GetProperty("rowVersion").GetString().Should().Be(before.GetProperty("rowVersion").GetString());
    }

    [Fact]
    public async Task ActiveChild_ChangeSourceFieldScale_StillWorks_WhenTheParentIsActive()
    {
        var p = await MakeRollupPair();

        (await PutField(p.Qty.Id, "decimal", p.Qty.BlockId, new { scale = 2 })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── 刪欄位、刪區塊會從記錄、索引與歷程拿掉值 ──

    private DynamicDbContextScope Db() => new(_app);

    private sealed class DynamicDbContextScope(TestApp app) : IDisposable
    {
        private readonly IServiceScope _scope = app.Services.CreateScope();
        public DynamicDbContext Db => _scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        public void Dispose() => _scope.Dispose();
    }

    private string ValuesJsonOf(long recordId)
    {
        using var scope = Db();
        return scope.Db.Set<DynamicModules.Domain.Entities.Dynamic.Record>().Single(r => r.Id == recordId).ValuesJson;
    }

    /// <summary>軟刪除一筆有欄位值的記錄後，欄位停用：這時刪除欄位會從那筆記錄的值裡拿掉鍵（purge）。</summary>
    private async Task<Sample> MakeSampleWithSoftDeletedValue()
    {
        var s = await MakeSample();
        var rec = await RecordOf(s.RecordId);
        (await _admin.DeleteAsync($"{Base}/records/{s.RecordId}?rowVersion={rec.GetProperty("rowVersion").GetString()}")).StatusCode
            .Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
        (await _admin.PutAsJsonAsync($"{Base}/fields/{s.Name2.Id}/status", new { status = "Inactive" })).StatusCode.Should().Be(HttpStatusCode.OK);
        return s;
    }

    private async Task<int> PurgedCount(long fieldId)
    {
        var check = await JsonOf(await _admin.GetAsync($"{Base}/fields/{fieldId}/delete-check"));
        var purge = check.GetProperty("purge");
        return purge.GetProperty("records").GetInt32();
    }

    [Fact]
    public async Task Closed_DeleteFieldThatStillHasValuesInRecords_IsRejected_ButWorksOnceReopened()
    {
        var s = await MakeSampleWithSoftDeletedValue();
        var purged = await PurgedCount(s.Name2.Id);
        purged.Should().BeGreaterThan(0, "測試前提：軟刪除記錄還留著這個欄位的值");
        SetStatus(s.Key, ModuleStatus.Closed);
        var before = ValuesJsonOf(s.RecordId);

        var res = await _admin.DeleteAsync($"{Base}/fields/{s.Name2.Id}?purged={purged}");

        await ShouldBeConflict(res);
        ValuesJsonOf(s.RecordId).Should().Be(before).And.Contain(s.Name2.Key);

        SetStatus(s.Key, ModuleStatus.Active);
        (await _admin.DeleteAsync($"{Base}/fields/{s.Name2.Id}?purged={purged}")).StatusCode
            .Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
        ValuesJsonOf(s.RecordId).Should().NotContain(s.Name2.Key);
    }

    [Fact]
    public async Task Closed_DeleteAFieldThatNeverHeldData_IsAllowed_LikeAnInactiveModule()
    {
        var s = await MakeSample();
        var extra = await AddField(_admin, s.Key, "text");
        (await _admin.PutAsJsonAsync($"{Base}/fields/{extra.Id}/status", new { status = "Inactive" })).StatusCode.Should().Be(HttpStatusCode.OK);
        SetStatus(s.Key, ModuleStatus.Closed);

        (await _admin.DeleteAsync($"{Base}/fields/{extra.Id}?purged=0")).StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Closed_DeleteBlockWhoseFieldsStillHaveValues_IsRejected_AndAnEmptyBlockCanGo()
    {
        var s = await MakeSample();
        var label = new Dictionary<string, string> { ["zh-TW"] = "區塊" };
        var emptyBlock = (await JsonOf(await _admin.PostAsJsonAsync($"{Base}/modules/{s.Key}/blocks", new { label }))).GetProperty("id").GetInt64();
        var dataBlock = (await JsonOf(await _admin.PostAsJsonAsync($"{Base}/modules/{s.Key}/blocks", new { label }))).GetProperty("id").GetInt64();
        var field = await JsonOf(await _admin.PostAsJsonAsync($"{Base}/modules/{s.Key}/fields",
            new { blockId = dataBlock, type = "text", label, config = new { } }));
        var fieldKey = field.GetProperty("key").GetString()!;
        var rec = await JsonOf(await _admin.PostAsJsonAsync($"{Base}/modules/{s.Key}/records",
            new { values = new Dictionary<string, object> { [s.TitleKey] = "有值", [fieldKey] = "待清除" } }));
        var recId = rec.GetProperty("id").GetInt64();
        (await _admin.DeleteAsync($"{Base}/records/{recId}?rowVersion={rec.GetProperty("rowVersion").GetString()}")).StatusCode
            .Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
        (await _admin.PutAsJsonAsync($"{Base}/fields/{field.GetProperty("id").GetInt64()}/status", new { status = "Inactive" })).StatusCode
            .Should().Be(HttpStatusCode.OK);
        var check = await JsonOf(await _admin.GetAsync($"{Base}/blocks/{dataBlock}/delete-check"));
        var purged = check.GetProperty("purge").GetProperty("records").GetInt32();
        purged.Should().BeGreaterThan(0);
        SetStatus(s.Key, ModuleStatus.Closed);

        await ShouldBeConflict(await _admin.DeleteAsync($"{Base}/blocks/{dataBlock}?purged={purged}"));
        ValuesJsonOf(recId).Should().Contain(fieldKey);

        (await _admin.DeleteAsync($"{Base}/blocks/{emptyBlock}?purged=0")).StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
    }
}
