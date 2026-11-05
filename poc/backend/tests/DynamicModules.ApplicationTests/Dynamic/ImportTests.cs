using System.Text;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ExportService;
using DynamicModules.Application.Dynamic.ExportService.Queries.ExportRecords;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.ImportService.Commands.ImportRecords;
using DynamicModules.Application.Dynamic.ImportService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RuleService.Commands.SaveRules;
using DynamicModules.Application.Dynamic.RuleService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>第 7 階段決策 6、7：CSV 匯入（預覽、確認）與 RecordCreator。</summary>
public class ImportTests : DesignerTestBase
{
    private static byte[] Csv(params string[] lines) => Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n");

    private async Task<string> TitleKey(string module) => $"f_{(await Detail(module)).TitleFieldId}";

    private async Task<ImportPreviewResponse> Preview(string module, byte[] content)
    {
        var r = await Send(new PreviewImportRequest(module, content));
        r.IsSuccess.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (ImportPreviewResponse)r.Data!;
    }

    private async Task<ImportResultResponse> Commit(string module, byte[] content)
    {
        var r = await Send(new ImportRecordsRequest(module, content));
        r.IsSuccess.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (ImportResultResponse)r.Data!;
    }

    private async Task<List<Record>> Records(string module)
    {
        var id = (await Db.Context.Set<Module>().AsNoTracking().SingleAsync(m => m.Key == module)).Id;
        return await Db.Context.Set<Record>().AsNoTracking().Where(r => r.ModuleId == id).OrderBy(r => r.Id).ToListAsync();
    }

    private static Dictionary<string, JsonElement> Values(Record r) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(r.ValuesJson)!;

    private async Task<OptionResponse> Option(FieldResponse field, string label) =>
        (OptionResponse)(await Send(new AddOptionRequest(field.Id, Text(label), null))).Data!;

    private async Task<long> SalesGroupId() =>
        (await Db.Context.Set<PermissionGroup>().SingleAsync(g => g.Key == "sales")).Id;

    private async Task Grant(string module, bool add = true, bool read = true)
    {
        var m = await Detail(module);
        Db.Context.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
        {
            GroupId = await SalesGroupId(), ProgramCode = m.ProgramCode,
            CanRead = read, CanAdd = add, CanEdit = true, CanDelete = true, CanPrint = true, CanAmt = true
        });
        await Db.Context.SaveChangesAsync();
    }

    private async Task<FieldResponse> AddBilingual(string module, FieldType type, string zh, string en, FieldConfig? config = null)
    {
        var m = await Detail(module);
        var r = await Send(new AddFieldRequest(module, m.Blocks[0].Id, type,
            new I18nText([KeyValuePair.Create("zh-TW", zh), KeyValuePair.Create("en-US", en)]), config ?? new FieldConfig()));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (FieldResponse)r.Data!;
    }

    [Fact]
    public async Task Preview_MapsHeadersByKeyOrAnyLocaleLabel_ReportsIgnored()
    {
        await NewModule("memo");
        var qty = await AddBilingual("memo", FieldType.Integer, "數量", "Qty");
        var price = await MustAdd("memo", FieldType.Decimal, label: "單價");
        await MustAdd("memo", FieldType.Formula, new FieldConfig { Formula = $"{{{qty.Id}}}*{{{price.Id}}}", ResultType = "number" }, "金額");
        await MustAdd("memo", FieldType.Text, new FieldConfig { ReadOnly = true }, "唯讀");
        (await Send(new ActivateModuleRequest("memo"))).IsSuccess.Should().BeTrue();
        var title = await TitleKey("memo");

        var p = await Preview("memo", Csv($"{title}, qty ,單價,金額,唯讀,亂打", "a,1,2,x,y,z"));

        p.Columns.Should().Equal(
            new ImportColumn(title, title, null),
            new ImportColumn("qty", qty.Key, null),
            new ImportColumn("單價", price.Key, null),
            new ImportColumn("金額", null, "系統計算的欄位"),
            new ImportColumn("唯讀", null, "唯讀欄位"),
            new ImportColumn("亂打", null, "找不到這個欄位"));
        p.Total.Should().Be(1);
        p.Valid.Should().Be(1);
        p.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Preview_DuplicateColumn_400()
    {
        await NewModule("memo");
        var qty = await MustAdd("memo", FieldType.Integer, label: "數量");
        (await Send(new ActivateModuleRequest("memo"))).IsSuccess.Should().BeTrue();

        var r = await Send(new PreviewImportRequest("memo", Csv($"數量,{qty.Key}", "1,2")));

        r.IsSuccess.Should().BeFalse();
        Err(r, "file").Should().Be("欄位「數量」重複");
    }

    [Fact]
    public async Task Preview_LabelMatchesSeveralFields_ColumnIgnored_KeyStillMaps()
    {
        await NewModule("memo");
        var qty = await AddBilingual("memo", FieldType.Integer, "數量", "Qty");
        await MustAdd("memo", FieldType.Text, label: "qty");
        (await Send(new ActivateModuleRequest("memo"))).IsSuccess.Should().BeTrue();
        var title = await TitleKey("memo");

        var p = await Preview("memo", Csv($"{title},QTY,{qty.Key}", "a,x,3"));

        p.Columns.Should().Equal(
            new ImportColumn(title, title, null),
            new ImportColumn("QTY", null, "對到多個欄位，請改用欄位代碼"),
            new ImportColumn(qty.Key, qty.Key, null));
        p.Valid.Should().Be(1);
    }

    [Fact]
    public async Task Preview_FileErrors_400()
    {
        await NewModule("memo", activate: true);

        Err(await Send(new PreviewImportRequest("memo", [])), "file").Should().Be("請選擇檔案");
        Err(await Send(new PreviewImportRequest("memo", new byte[1024 * 1024 + 1])), "file").Should().Be("檔案不能超過 1 MB");
        Err(await Send(new PreviewImportRequest("memo", [0xB4, 0xFA, 0x0D, 0x0A])), "file").Should().Be("請把檔案存成 UTF-8 的 CSV");
        Err(await Send(new ImportRecordsRequest("memo", [])), "file").Should().Be("請選擇檔案");
    }

    [Fact]
    public async Task Preview_ReportsRowErrorsByLine_WritesNothing()
    {
        await NewModule("memo");
        var status = await MustAdd("memo", FieldType.Enum, label: "狀態");
        await Option(status, "送審");
        (await Send(new ActivateModuleRequest("memo"))).IsSuccess.Should().BeTrue();
        var title = await TitleKey("memo");

        var p = await Preview("memo", Csv($"{title},狀態", "a,送審", ",送審", "c,", "d,不存在"));

        p.Total.Should().Be(4);
        p.Valid.Should().Be(2);
        p.Errors.Select(e => e.Line).Should().Equal(3, 5);
        p.Errors[0].Errors.Should().ContainKey(title).WhoseValue.Should().Equal("此欄位必填");
        p.Errors[1].Errors.Should().ContainKey(status.Key).WhoseValue.Should().Equal("找不到選項「不存在」");
        (await Records("memo")).Should().BeEmpty();
    }

    [Fact]
    public async Task Preview_HiddenField_TreatedAsUnknown()
    {
        await NewModule("memo", activate: true);
        var secret = await MustAdd("memo", FieldType.Text, label: "機密");
        (await Send(new SaveModulePermissionsRequest("memo", new ModulePermissionsDto(
            [], [], [new FieldPermissionDto(secret.Id, await SalesGroupId(), FieldAccess.Hidden)], [])))).IsSuccess.Should().BeTrue();
        await Grant("memo");
        var title = await TitleKey("memo");

        As("sales01");
        var p = await Preview("memo", Csv($"{title},機密,{secret.Key}", "a,b,c"));

        p.Columns.Skip(1).Should().Equal(
            new ImportColumn("機密", null, "找不到這個欄位"),
            new ImportColumn(secret.Key, null, "找不到這個欄位"));
    }

    [Fact]
    public async Task Preview_PermissionReadOnlyField_Ignored()
    {
        await NewModule("memo", activate: true);
        var locked = await MustAdd("memo", FieldType.Text, label: "鎖定");
        (await Send(new SaveModulePermissionsRequest("memo", new ModulePermissionsDto(
            [], [], [new FieldPermissionDto(locked.Id, await SalesGroupId(), FieldAccess.ReadOnly)], [])))).IsSuccess.Should().BeTrue();
        await Grant("memo");

        As("sales01");
        var p = await Preview("memo", Csv("鎖定", "x"));

        p.Columns.Should().Equal(new ImportColumn("鎖定", null, "唯讀欄位"));
    }

    [Fact]
    public async Task Preview_AppliesRulesAndOptionScope()
    {
        await NewModule("memo");
        var qty = await MustAdd("memo", FieldType.Integer, label: "數量");
        var note = await MustAdd("memo", FieldType.Text, label: "備註");
        var status = await MustAdd("memo", FieldType.Enum, label: "狀態");
        var a = await Option(status, "甲");
        await Option(status, "乙");
        (await Send(new SaveRulesRequest("memo",
        [
            new RuleInput(RuleKind.Required, J($"{{\"all\":[{{\"field\":{qty.Id},\"op\":\"gt\",\"value\":5}}]}}"),
                [$"field:{note.Id}"], null, null, true),
            new RuleInput(RuleKind.Validation, J($"{{\"all\":[{{\"field\":{qty.Id},\"op\":\"gt\",\"value\":0}}]}}"),
                [$"field:{qty.Id}"], null, Text("數量必須大於 0"), true),
            new RuleInput(RuleKind.OptionFilter, J("{\"all\":[]}"), [$"field:{status.Id}"], [a.Id], null, true),
        ]))).IsSuccess.Should().BeTrue();
        (await Send(new ActivateModuleRequest("memo"))).IsSuccess.Should().BeTrue();
        var title = await TitleKey("memo");

        var p = await Preview("memo", Csv($"{title},數量,備註,狀態", "a,9,,甲", "b,0,x,甲", "c,1,x,乙"));

        p.Errors.Select(e => e.Line).Should().Equal(2, 3, 4);
        // 與 POST records 相同的錯誤
        var opts = (await Detail("memo")).Blocks[0].Fields.Single(f => f.Id == status.Id).Options;
        var posts = new[]
        {
            Vals((title, "a"), (qty.Key, 9), (note.Key, null), (status.Key, a.Value)),
            Vals((title, "b"), (qty.Key, 0), (note.Key, "x"), (status.Key, a.Value)),
            Vals((title, "c"), (qty.Key, 1), (note.Key, "x"), (status.Key, opts.Single(o => o.Id != a.Id).Value)),
        };
        for (var i = 0; i < posts.Length; i++)
        {
            var post = await Send(new CreateRecordRequest("memo", posts[i]));
            p.Errors[i].Errors.Should().BeEquivalentTo(post.Errors);
        }
        p.Errors[0].Errors[note.Key].Should().Equal("此欄位必填");
        p.Errors[1].Errors[qty.Key].Should().Equal("數量必須大於 0");
        p.Errors[2].Errors[status.Key].Should().Equal("選項不在可選範圍");
    }

    private static Dictionary<string, JsonElement?> Vals(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => (JsonElement?)JsonSerializer.SerializeToElement(p.Value));

    [Fact]
    public async Task Commit_AnyRowInvalid_400_WritesNothing()
    {
        await NewModule("memo", activate: true);
        var title = await TitleKey("memo");

        var r = await Send(new ImportRecordsRequest("memo", Csv(title, "a", "", "b"))); // 空白列略過，不算列數

        r.IsSuccess.Should().BeTrue("空白列不算資料");
        var bad = await Send(new ImportRecordsRequest("memo", Csv($"{title},{title}x", "c,1", ",2")));
        bad.IsSuccess.Should().BeFalse();
        bad.Errors.Keys.Should().Equal($"rows[3].{title}");
        (await Records("memo")).Select(x => x.Title).Should().Equal("a", "b");
    }

    [Fact]
    public async Task Commit_BlankCell_NotProvided_DefaultAppliesAndRequiredFires()
    {
        await NewModule("memo");
        var qty = await MustAdd("memo", FieldType.Integer, new FieldConfig { DefaultValue = J("5") }, "數量");
        (await Send(new ActivateModuleRequest("memo"))).IsSuccess.Should().BeTrue();
        var title = await TitleKey("memo");

        var bad = await Send(new ImportRecordsRequest("memo", Csv($"{title},數量", "a,", " ,3")));
        bad.Errors.Keys.Should().Equal($"rows[3].{title}");
        Err(bad, $"rows[3].{title}").Should().Be("此欄位必填");

        await Commit("memo", Csv($"{title},數量", "a,  "));
        Values((await Records("memo")).Single())[qty.Key].GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task Commit_CreatesAll_WithAutonumberOwnerHistory()
    {
        await NewModule("memo");
        var no = await MustAdd("memo", FieldType.AutoNumber, new FieldConfig { Format = "{000}" }, "編號");
        (await Send(new ActivateModuleRequest("memo"))).IsSuccess.Should().BeTrue();
        await Grant("memo");
        var title = await TitleKey("memo");

        As("sales01");
        var result = await Commit("memo", Csv(title, "a", "b", "c"));

        result.Created.Should().Be(3);
        var records = await Records("memo");
        records.Select(r => r.Title).Should().Equal("a", "b", "c");
        records.Select(r => Values(r)[no.Key].GetString()).Should().Equal("001", "002", "003");
        records.Should().OnlyContain(r => r.OwnerUserId == "sales01" && r.CreatedUserId == "sales01");
        var ids = records.Select(r => r.Id).ToList();
        var history = await Db.Context.Set<RecordHistory>().AsNoTracking().Where(h => ids.Contains(h.RecordId)).ToListAsync();
        history.Should().HaveCount(3).And.OnlyContain(h => h.Operation == RecordHistoryOperation.Create && h.ChangedUserId == "sales01");
    }

    [Fact]
    public async Task Commit_ChildOfLookupRollup_RecalculatesParent()
    {
        var req = await NewModule("req");
        var rec = await NewModule("rec");
        var source = await MustAdd(rec, FieldType.Reference, RefTo(req), "申請");
        var hours = await MustAdd(rec, FieldType.Decimal, new FieldConfig { Scale = 2 }, "工時");
        var total = await MustAdd(req, FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(source.Id, RollupFunction.Sum, hours.Id, null) }, "總工時");
        (await Send(new ActivateModuleRequest(req))).IsSuccess.Should().BeTrue();
        (await Send(new ActivateModuleRequest(rec))).IsSuccess.Should().BeTrue();
        var parent = (DynamicRecordResponse)(await Send(new CreateRecordRequest(req, Vals((await TitleKey(req), "申請甲"))))).Data!;

        await Commit(rec, Csv($"{await TitleKey(rec)},申請,工時", "r1,申請甲,2", "r2,申請甲,3.5"));

        var stored = await Db.Context.Set<Record>().AsNoTracking().SingleAsync(r => r.Id == parent.Id);
        Values(stored)[total.Key].GetDecimal().Should().Be(5.5m);
    }

    [Fact]
    public async Task Preview_ReferenceTitles_ModuleAndEntity_NotFoundAndDuplicate()
    {
        var tgt = await NewModule("tgt", activate: true);
        var tt = await TitleKey(tgt);
        foreach (var t in new[] { "甲", "乙", "乙", "甲乙" })
            (await Send(new CreateRecordRequest(tgt, Vals((tt, t))))).IsCreated.Should().BeTrue();
        await NewModule("src");
        var toTgt = await MustAdd("src", FieldType.Reference, RefTo(tgt), "目標");
        var mold = await MustAdd("src", FieldType.Reference,
            new FieldConfig { RefTarget = new ReferenceTarget(ReferenceTarget.EntityKind, "design.mold") }, "模具");
        (await Send(new ActivateModuleRequest("src"))).IsSuccess.Should().BeTrue();
        var title = await TitleKey("src");

        var p = await Preview("src", Csv($"{title},目標,模具", "a,甲,M-0002 外殼下蓋模", "b,乙,M-0002", "c,丙,"));

        p.Valid.Should().Be(1);
        p.Errors.Select(e => e.Line).Should().Equal(3, 4);
        p.Errors[0].Errors[toTgt.Key].Should().Equal("「乙」有多筆同名資料");
        p.Errors[0].Errors[mold.Key].Should().Equal("找不到「M-0002」");
        p.Errors[1].Errors[toTgt.Key].Should().Equal("找不到「丙」");
    }

    [Fact]
    public async Task Preview_ReferenceTargetWithoutRead_NotFound()
    {
        var tgt = await NewModule("tgt", activate: true);
        (await Send(new CreateRecordRequest(tgt, Vals((await TitleKey(tgt), "甲"))))).IsCreated.Should().BeTrue();
        await NewModule("src");
        var toTgt = await MustAdd("src", FieldType.Reference, RefTo(tgt), "目標");
        (await Send(new ActivateModuleRequest("src"))).IsSuccess.Should().BeTrue();
        await Grant("src");
        var title = await TitleKey("src");

        As("sales01");
        var p = await Preview("src", Csv($"{title},目標", "a,甲"));

        p.Errors.Single().Errors[toTgt.Key].Should().Equal("找不到「甲」");
    }

    [Fact]
    public async Task Preview_DuplicateUserDisplayName_NotFound_UserIdStillWorks()
    {
        await NewModule("memo");
        var who = await MustAdd("memo", FieldType.User, label: "負責人");
        (await Send(new ActivateModuleRequest("memo"))).IsSuccess.Should().BeTrue();
        Db.Context.Set<AppUser>().Add(new AppUser { UserId = "sales09", DisplayName = "業務一" });
        await Db.Context.SaveChangesAsync();
        var title = await TitleKey("memo");

        var p = await Preview("memo", Csv($"{title},負責人", "a,業務一", "b,sales01", "c,業務二"));

        p.Errors.Select(e => e.Line).Should().Equal(2);
        p.Errors[0].Errors[who.Key].Should().Equal("找不到使用者「業務一」");
    }

    [Fact]
    public async Task RoundTrip_ExportThenImport_SameValues()
    {
        var tgt = await NewModule("tgt", activate: true);
        var target = (DynamicRecordResponse)(await Send(new CreateRecordRequest(tgt, Vals((await TitleKey(tgt), "目標甲"))))).Data!;
        await NewModule("src");
        var status = await MustAdd("src", FieldType.Enum, label: "狀態");
        var tags = await MustAdd("src", FieldType.MultiEnum, label: "標籤");
        var who = await MustAdd("src", FieldType.User, label: "負責人");
        var refF = await MustAdd("src", FieldType.Reference, RefTo(tgt), "目標");
        var flag = await MustAdd("src", FieldType.Boolean, label: "急件");
        var rate = await MustAdd("src", FieldType.Percent, label: "比率");
        var day = await MustAdd("src", FieldType.Date, label: "日期");
        var at = await MustAdd("src", FieldType.DateTime, label: "時間");
        var qty = await MustAdd("src", FieldType.Integer, label: "數量");
        var no = await MustAdd("src", FieldType.AutoNumber, new FieldConfig { Format = "{000}" }, "編號");
        var twice = await MustAdd("src", FieldType.Formula, new FieldConfig { Formula = $"{{{qty.Id}}}*2", ResultType = "number" }, "兩倍");
        var s1 = await Option(status, "送審");
        var t1 = await Option(tags, "紅");
        var t2 = await Option(tags, "藍");
        (await Send(new ActivateModuleRequest("src"))).IsSuccess.Should().BeTrue();
        var title = await TitleKey("src");
        var created = await Send(new CreateRecordRequest("src", Vals(
            (title, "=x"), (status.Key, s1.Value), (tags.Key, new[] { t1.Value, t2.Value }), (who.Key, "sales01"),
            (refF.Key, target.Id), (flag.Key, true), (rate.Key, 12.5m), (day.Key, "2026-11-04"),
            (at.Key, "2026-11-04T09:30:00+08:00"), (qty.Key, 7))));
        created.IsCreated.Should().BeTrue(string.Join(";", created.Errors.Select(e => e.Key + e.Value[0])));
        var original = (await Records("src")).Single();

        var file = (ExportFile)(await Send(new ExportRecordsRequest("src", "csv", null, [], [], null, "zh-TW"))).Data!;
        Encoding.UTF8.GetString(file.Content).Should().Contain("'=x");
        (await Commit("src", file.Content)).Created.Should().Be(1);

        var copy = (await Records("src")).Last();
        copy.Id.Should().NotBe(original.Id);
        var before = Values(original);
        var after = Values(copy);
        before.Keys.Should().Contain([status.Key, tags.Key, who.Key, refF.Key, flag.Key, rate.Key, day.Key, at.Key]);
        foreach (var key in before.Keys.Where(k => k != no.Key && k != twice.Key))
            after[key].GetRawText().Should().Be(before[key].GetRawText(), key);
        after.Keys.Should().BeEquivalentTo(before.Keys);
        after[no.Key].GetString().Should().Be("002");
        copy.Title.Should().Be("=x");
    }

    [Fact]
    public async Task RoundTrip_ReferenceTitleStartingWithEquals()
    {
        var tgt = await NewModule("tgt", activate: true);
        var target = (DynamicRecordResponse)(await Send(new CreateRecordRequest(tgt, Vals((await TitleKey(tgt), "=1+1"))))).Data!;
        await NewModule("src");
        var refF = await MustAdd("src", FieldType.Reference, RefTo(tgt), "目標");
        (await Send(new ActivateModuleRequest("src"))).IsSuccess.Should().BeTrue();
        (await Send(new CreateRecordRequest("src", Vals((await TitleKey("src"), "a"), (refF.Key, target.Id))))).IsCreated.Should().BeTrue();

        var file = (ExportFile)(await Send(new ExportRecordsRequest("src", "csv", null, [], [], null, "zh-TW"))).Data!;
        Encoding.UTF8.GetString(file.Content).Should().Contain("'=1+1");
        (await Commit("src", file.Content)).Created.Should().Be(1);

        Values((await Records("src")).Last())[refF.Key].GetInt64().Should().Be(target.Id);
    }

    [Fact]
    public async Task Import_WithoutAdd_403()
    {
        await NewModule("memo", activate: true);
        await Grant("memo", add: false);
        As("sales01");

        (await Send(new PreviewImportRequest("memo", Csv("a", "b")))).IsForbidden.Should().BeTrue();
        (await Send(new ImportRecordsRequest("memo", Csv("a", "b")))).IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task Import_DetailModule_404()
    {
        await NewModule("order", activate: true);
        await NewModule("line");
        await MustAdd("line", FieldType.Reference, RefTo("order", ReferenceRelation.Detail), "表頭");
        (await Send(new ActivateModuleRequest("line"))).IsSuccess.Should().BeTrue();

        var r = await Send(new PreviewImportRequest("line", Csv("a", "b")));

        r.IsNotFound.Should().BeTrue();
        r.Message.Should().Be("模組不存在或未啟用");
        (await Send(new ImportRecordsRequest("nope", Csv("a", "b")))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task Import_Extension_400()
    {
        await NewModule("ext", activate: true, kind: ModuleKind.Extension, host: "design.mold");

        var r = await Send(new ImportRecordsRequest("ext", Csv("a", "b")));

        r.IsSuccess.Should().BeFalse();
        Err(r, "file").Should().Be("擴充模組不支援匯入");
    }
}
