using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.DeleteService.Commands.DeleteBlock;
using DynamicModules.Application.Dynamic.DeleteService.Commands.DeleteField;
using DynamicModules.Application.Dynamic.DeleteService.Commands.DeleteModule;
using DynamicModules.Application.Dynamic.DeleteService.Data;
using DynamicModules.Application.Dynamic.DeleteService.Queries.CheckDelete;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddBlock;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.DeactivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic;

public class SafeDeleteTests : DesignerTestBase
{
    private DynamicModules.Domain.Data.DynamicDbContext Ctx => Db.Context;

    /// <summary>每個請求前清掉追蹤，模擬正式環境每個 HTTP 請求一個新的 DbContext。</summary>
    private new Task<TRes> Send<TRes>(MediatR.IRequest<TRes> request)
    {
        Ctx.ChangeTracker.Clear();
        return base.Send(request);
    }

    private static DeleteCheckResponse CheckOf(ServiceResult r) => (DeleteCheckResponse)r.Data!;

    private static DeleteBlocker Blocker(ServiceResult r, string kind) => CheckOf(r).Blockers.Should().Contain(b => b.Kind == kind).Subject;

    private async Task<long> ModuleId(string key) => (await Ctx.Set<Module>().AsNoTracking().SingleAsync(m => m.Key == key)).Id;

    private async Task<bool> ModuleExists(string key) => await Ctx.Set<Module>().AsNoTracking().AnyAsync(m => m.Key == key);

    /// <summary>建立一筆記錄（標題與指定欄位值），回傳記錄回應。</summary>
    private async Task<DynamicRecordResponse> NewRecord(string module, params (FieldResponse Field, string Json)[] values)
    {
        var m = await Detail(module);
        var dict = new Dictionary<string, JsonElement?> { [$"f_{m.TitleFieldId}"] = J("\"x\"") };
        foreach (var (f, json) in values) dict[f.Key] = J(json);
        var r = await Send(new CreateRecordRequest(module, dict));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (DynamicRecordResponse)r.Data!;
    }

    private async Task SoftDelete(DynamicRecordResponse rec) =>
        (await Send(new DeleteRecordRequest(rec.Id, rec.RowVersion))).IsSuccess.Should().BeTrue();

    private async Task Stop(string module) => (await Send(new DeactivateModuleRequest(module))).IsSuccess.Should().BeTrue();

    // ───────────── 模組 ─────────────

    [Fact]
    public async Task Module_WithLiveRecord_IsBlocked()
    {
        await NewModule("cust", activate: true);
        await NewRecord("cust");
        await Stop("cust");

        var r = await Send(new DeleteModuleRequest("cust", 0));

        r.IsConflict.Should().BeTrue();
        Blocker(r, "liveRecords").Count.Should().Be(1);
        CheckOf(r).CanDelete.Should().BeFalse();
        (await ModuleExists("cust")).Should().BeTrue();
        (await Ctx.Set<Record>().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Module_Active_IsBlocked()
    {
        await NewModule("cust", activate: true);

        var r = await Send(new DeleteModuleRequest("cust", 0));

        r.IsConflict.Should().BeTrue();
        Blocker(r, "moduleActive");
        (await ModuleExists("cust")).Should().BeTrue();
    }

    [Fact]
    public async Task Module_Draft_WithNoData_IsDeleted()
    {
        await NewModule("cust");

        (await Send(new DeleteModuleRequest("cust", 0))).IsSuccess.Should().BeTrue();

        (await ModuleExists("cust")).Should().BeFalse();
        (await Ctx.Set<Field>().CountAsync()).Should().Be(0);
        (await Ctx.Set<Block>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Module_OnlySoftDeletedRecords_IsDeletedAndEverythingPurged()
    {
        await NewModule("cust", activate: true);
        var status = await MustAdd("cust", FieldType.Enum, new FieldConfig(), "狀態");
        await Send(new DynamicModules.Application.Dynamic.FieldService.Commands.AddOption.AddOptionRequest(status.Id, Text("新"), null));
        await MustAdd("cust", FieldType.AutoNumber, new FieldConfig { Format = "C-{0000}" }, "編號");
        var name = await MustAdd("cust", FieldType.Text, new FieldConfig { IsFilterable = true }, "名稱");
        var a = await NewRecord("cust", (name, "\"甲\""));
        var b = await NewRecord("cust", (name, "\"乙\""));
        await SoftDelete(a);
        await SoftDelete(b);
        await Stop("cust");

        var moduleId = await ModuleId("cust");
        var code = (await Ctx.Set<Module>().AsNoTracking().SingleAsync(m => m.Id == moduleId)).ProgramCode;
        var group = await Ctx.Set<PermissionGroup>().FirstAsync();
        Ctx.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege { GroupId = group.Id, ProgramCode = code, CanRead = true });
        Ctx.Set<ModuleDataScope>().Add(new ModuleDataScope { ModuleId = moduleId, GroupId = group.Id, Scope = DataScope.All });
        Ctx.Set<FieldPermission>().Add(new FieldPermission { FieldId = name.Id, GroupId = group.Id, Access = FieldAccess.Hidden });
        Ctx.Set<ListView>().Add(new ListView { ModuleId = moduleId, NameI18n = Text("v"), Columns = [name.Id], Sort = [] });
        await Ctx.SaveChangesAsync();
        (await Ctx.Set<RecordHistory>().CountAsync()).Should().BeGreaterThan(0);
        (await Ctx.Set<RecordIndex>().CountAsync()).Should().BeGreaterThan(0);
        (await Ctx.Set<NumberSequence>().CountAsync()).Should().BeGreaterThan(0);

        // 另一個模組的資料不受影響
        await NewModule("other", activate: true);
        await NewRecord("other");

        var check = CheckOf(await Send(new CheckDeleteRequest(DeleteTarget.Module, "cust", 0)));
        check.CanDelete.Should().BeTrue();
        check.Purge.Records.Should().Be(2);
        check.Purge.HistoryRows.Should().BeGreaterThan(0);

        (await Send(new DeleteModuleRequest("cust", 2))).IsSuccess.Should().BeTrue();

        (await ModuleExists("cust")).Should().BeFalse();
        (await Ctx.Set<Record>().CountAsync(r => r.ModuleId == moduleId)).Should().Be(0);
        (await Ctx.Set<Field>().CountAsync(f => f.ModuleId == moduleId)).Should().Be(0);
        (await Ctx.Set<Block>().CountAsync(x => x.ModuleId == moduleId)).Should().Be(0);
        (await Ctx.Set<ListView>().CountAsync(v => v.ModuleId == moduleId)).Should().Be(0);
        (await Ctx.Set<FieldOption>().CountAsync(o => o.FieldId == status.Id)).Should().Be(0);
        (await Ctx.Set<NumberSequence>().CountAsync(n => n.ModuleId == moduleId)).Should().Be(0);
        (await Ctx.Set<ModuleDataScope>().CountAsync(s => s.ModuleId == moduleId)).Should().Be(0);
        (await Ctx.Set<FieldPermission>().CountAsync(p => p.FieldId == name.Id)).Should().Be(0);
        (await Ctx.Set<GroupProgramPrivilege>().CountAsync(p => p.ProgramCode == code)).Should().Be(0);
        (await Ctx.Set<DynamicProgram>().CountAsync(p => p.ProgramCode == code)).Should().Be(0);
        // 索引與歷程只剩另一個模組的
        var otherIds = await Ctx.Set<Record>().Select(r => r.Id).ToListAsync();
        otherIds.Should().HaveCount(1);
        (await Ctx.Set<RecordIndex>().CountAsync(i => !otherIds.Contains(i.RecordId))).Should().Be(0);
        (await Ctx.Set<RecordHistory>().CountAsync(h => !otherIds.Contains(h.RecordId))).Should().Be(0);
        (await ModuleExists("other")).Should().BeTrue();
    }

    [Fact]
    public async Task Module_PurgedCountMismatch_Conflicts_AndDeletesNothing()
    {
        await NewModule("cust", activate: true);
        await SoftDelete(await NewRecord("cust"));
        await SoftDelete(await NewRecord("cust"));
        await Stop("cust");

        var r = await Send(new DeleteModuleRequest("cust", 1));

        r.IsConflict.Should().BeTrue();
        CheckOf(r).CanDelete.Should().BeTrue();
        CheckOf(r).Purge.Records.Should().Be(2);
        (await ModuleExists("cust")).Should().BeTrue();
        (await Ctx.Set<Record>().CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Module_MissingPurgedCount_IsInvalid()
    {
        await NewModule("cust");

        var r = await Send(new DeleteModuleRequest("cust", null));

        r.Errors.Should().ContainKey("purged");
        (await ModuleExists("cust")).Should().BeTrue();
    }

    [Fact]
    public async Task Module_Unknown_NotFound() =>
        (await Send(new DeleteModuleRequest("nope", 0))).IsNotFound.Should().BeTrue();

    [Fact]
    public async Task Module_ReferencedByReferenceField_ListsSource_ThenDeletableAfterSourceFieldRemoved()
    {
        await NewModule("cust");
        await NewModule("ord");
        var link = await MustAdd("ord", FieldType.Reference, RefTo("cust"), "客戶");

        var r = await Send(new DeleteModuleRequest("cust", 0));

        r.IsConflict.Should().BeTrue();
        var b = Blocker(r, "referenceField");
        b.ModuleKey.Should().Be("ord");
        b.Id.Should().Be(link.Id);
        b.Name.Should().Be("客戶");
        (await ModuleExists("cust")).Should().BeTrue();

        (await Deactivate(link.Id)).IsSuccess.Should().BeTrue();
        (await Send(new DeleteFieldRequest(link.Id, 0))).IsSuccess.Should().BeTrue();
        (await Send(new DeleteModuleRequest("cust", 0))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Module_ReferencedByDisabledReferenceField_StillBlocked()
    {
        await NewModule("cust");
        await NewModule("ord");
        var link = await MustAdd("ord", FieldType.Reference, RefTo("cust"), "客戶");
        (await Deactivate(link.Id)).IsSuccess.Should().BeTrue();

        Blocker(await Send(new DeleteModuleRequest("cust", 0)), "referenceField").Id.Should().Be(link.Id);
    }

    [Fact]
    public async Task Module_ReferencedByRollup_ListsSource()
    {
        await NewModule("req");
        await NewModule("rec");
        var source = await MustAdd("rec", FieldType.Reference, RefTo("req", ReferenceRelation.Lookup), "申請");
        // rec 被 req 的彙總引用；req 刪除會被 rec 的參照擋住，所以改刪 rec：它的欄位被 req 的彙總引用
        var roll = await MustAdd("req", FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(source.Id, RollupFunction.Count, null, null) }, "筆數");

        var r = await Send(new DeleteModuleRequest("rec", 0));

        r.IsConflict.Should().BeTrue();
        Blocker(r, "rollup").Id.Should().Be(roll.Id);
        (await ModuleExists("rec")).Should().BeTrue();
    }

    [Fact]
    public async Task Module_ReferencedByReport_ListsReport()
    {
        await NewModule("cust");
        var def = new ReportDefinition(1, "cust", null, [], [], [], [], null, null, [], "table");
        var report = new Report { OwnerUserId = "admin", Name = "客戶報表", DefinitionJson = ReportDefinition.Serialize(def), RowVersion = "x" };
        Ctx.Set<Report>().Add(report);
        await Ctx.SaveChangesAsync();

        var r = await Send(new DeleteModuleRequest("cust", 0));

        r.IsConflict.Should().BeTrue();
        var b = Blocker(r, "report");
        b.Name.Should().Be("客戶報表");
        b.Id.Should().Be(report.Id);
    }

    [Fact]
    public async Task Module_OwnInternalReferences_DoNotBlock()
    {
        await NewModule("cust");
        // 自己指向自己的參照欄位、自己的規則與檢視都跟著模組消失
        await MustAdd("cust", FieldType.Reference, RefTo("cust"), "上層");
        var m = await Detail("cust");
        Ctx.Set<ListView>().Add(new ListView { ModuleId = await ModuleId("cust"), NameI18n = Text("v"), Columns = [m.TitleFieldId!.Value], Sort = [] });
        await Ctx.SaveChangesAsync();

        (await Send(new DeleteModuleRequest("cust", 0))).IsSuccess.Should().BeTrue();
    }

    // ───────────── 欄位 ─────────────

    private async Task<(FieldResponse Name, FieldResponse Note)> CustWithFields()
    {
        await NewModule("cust", activate: true);
        var name = await MustAdd("cust", FieldType.Text, new FieldConfig { IsFilterable = true }, "名稱");
        var note = await MustAdd("cust", FieldType.Text, new FieldConfig(), "備註");
        return (name, note);
    }

    [Fact]
    public async Task Field_Active_IsBlocked()
    {
        var (name, _) = await CustWithFields();

        var r = await Send(new DeleteFieldRequest(name.Id, 0));

        r.IsConflict.Should().BeTrue();
        Blocker(r, "fieldActive");
        (await Ctx.Set<Field>().AnyAsync(f => f.Id == name.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Field_WithValueInLiveRecord_IsBlocked()
    {
        var (name, _) = await CustWithFields();
        await NewRecord("cust", (name, "\"甲\""));
        await Deactivate(name.Id);

        var r = await Send(new DeleteFieldRequest(name.Id, 0));

        r.IsConflict.Should().BeTrue();
        Blocker(r, "fieldData").Count.Should().Be(1);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"  \"")]
    public async Task Field_EmptyValuesInLiveRecords_CountAsNoData(string emptyJson)
    {
        var (name, _) = await CustWithFields();
        await NewRecord("cust", (name, emptyJson));
        await Deactivate(name.Id);

        (await Send(new DeleteFieldRequest(name.Id, 0))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Field_ZeroAndFalse_CountAsData()
    {
        await NewModule("cust", activate: true);
        var qty = await MustAdd("cust", FieldType.Integer, new FieldConfig(), "數量");
        var flag = await MustAdd("cust", FieldType.Boolean, new FieldConfig(), "旗標");
        await NewRecord("cust", (qty, "0"), (flag, "false"));
        await Deactivate(qty.Id);
        await Deactivate(flag.Id);

        Blocker(await Send(new DeleteFieldRequest(qty.Id, 0)), "fieldData");
        Blocker(await Send(new DeleteFieldRequest(flag.Id, 0)), "fieldData");
    }

    [Fact]
    public async Task Field_OnlySoftDeletedValues_IsDeletedAndDataPurged()
    {
        var (name, note) = await CustWithFields();
        var created = await NewRecord("cust", (name, "\"甲\""), (note, "\"留\""));
        var kept = await NewRecord("cust", (note, "\"留\""));
        // 更新一次才會有欄位層級歷程（建立與刪除的歷程沒有欄位）
        var upd = await Send(new UpdateRecordRequest(created.Id,
            new Dictionary<string, JsonElement?> { [name.Key] = J("\"甲2\"") }, created.RowVersion));
        upd.IsSuccess.Should().BeTrue();
        var gone = (DynamicRecordResponse)upd.Data!;
        await SoftDelete(gone);
        await Deactivate(name.Id);
        var group = await Ctx.Set<PermissionGroup>().FirstAsync();
        Ctx.Set<FieldPermission>().Add(new FieldPermission { FieldId = name.Id, GroupId = group.Id, Access = FieldAccess.Hidden });
        await Ctx.SaveChangesAsync();
        (await Ctx.Set<RecordIndex>().CountAsync(i => i.FieldId == name.Id)).Should().BeGreaterThan(0);
        (await Ctx.Set<RecordHistory>().CountAsync(h => h.FieldId == name.Id)).Should().BeGreaterThan(0);

        var check = CheckOf(await Send(new CheckDeleteRequest(DeleteTarget.Field, null, name.Id)));
        check.CanDelete.Should().BeTrue();
        check.Purge.Records.Should().Be(1);

        (await Send(new DeleteFieldRequest(name.Id, 1))).IsSuccess.Should().BeTrue();

        (await Ctx.Set<Field>().AnyAsync(f => f.Id == name.Id)).Should().BeFalse();
        (await Ctx.Set<RecordIndex>().CountAsync(i => i.FieldId == name.Id)).Should().Be(0);
        (await Ctx.Set<RecordHistory>().CountAsync(h => h.FieldId == name.Id)).Should().Be(0);
        (await Ctx.Set<FieldPermission>().CountAsync(p => p.FieldId == name.Id)).Should().Be(0);
        var rows = await Ctx.Set<Record>().AsNoTracking().ToListAsync();
        rows.Should().OnlyContain(r => !r.ValuesJson.Contains(name.Key));
        rows.Single(r => r.Id == kept.Id).ValuesJson.Should().Contain(note.Key);
        rows.Single(r => r.Id == gone.Id).ValuesJson.Should().Contain(note.Key);
    }

    [Fact]
    public async Task Field_Formula_IsDerived_DeletableDespiteLiveRecords_AndLiveValuesRemoved()
    {
        await NewModule("cust", activate: true);
        var qty = await MustAdd("cust", FieldType.Integer, new FieldConfig(), "數量");
        var calc = await MustAdd("cust", FieldType.Formula, new FieldConfig { Formula = $"{{{qty.Id}}} * 2", ResultType = "number" }, "加倍");
        var rec = await NewRecord("cust", (qty, "3"));
        (await Ctx.Set<Record>().AsNoTracking().SingleAsync()).ValuesJson.Should().Contain(calc.Key);
        await Deactivate(calc.Id);

        (await Send(new DeleteFieldRequest(calc.Id, 0))).IsSuccess.Should().BeTrue();

        var row = await Ctx.Set<Record>().AsNoTracking().SingleAsync();
        row.ValuesJson.Should().NotContain(calc.Key);
        row.ValuesJson.Should().Contain(qty.Key);
        row.RowVersion.Should().NotBe(rec.RowVersion);
    }

    [Fact]
    public async Task Field_TitleField_IsBlocked()
    {
        await NewModule("cust");
        var m = await Detail("cust");
        var r = await Send(new DeleteFieldRequest(m.TitleFieldId!.Value, 0));
        r.IsConflict.Should().BeTrue();
        Blocker(r, "titleField");
    }

    [Fact]
    public async Task Field_ReferencedByFormula_ListsFormula_EvenWhenFormulaDisabled()
    {
        await NewModule("cust");
        var qty = await MustAdd("cust", FieldType.Integer, new FieldConfig(), "數量");
        var calc = await MustAdd("cust", FieldType.Formula, new FieldConfig { Formula = $"{{{qty.Id}}} * 2", ResultType = "number" }, "加倍");
        // 先停用公式，才能再停用被引用的欄位；停用的公式仍算引用
        await Deactivate(calc.Id);
        await Deactivate(qty.Id);

        var r = await Send(new DeleteFieldRequest(qty.Id, 0));

        r.IsConflict.Should().BeTrue();
        var b = Blocker(r, "formula");
        b.Id.Should().Be(calc.Id);
        b.ModuleKey.Should().Be("cust");
    }

    [Fact]
    public async Task Field_ReferencedByRule_ListsRule()
    {
        var (name, note) = await CustWithFields();
        var rule = new Rule
        {
            ModuleId = await ModuleId("cust"), Kind = RuleKind.Required,
            Condition = J($"{{\"field\":{name.Id},\"op\":\"eq\",\"value\":\"x\"}}"), Targets = [$"field:{note.Id}"], Active = false
        };
        Ctx.Set<Rule>().Add(rule);
        await Ctx.SaveChangesAsync();
        await Deactivate(name.Id);
        await Deactivate(note.Id);

        Blocker(await Send(new DeleteFieldRequest(name.Id, 0)), "rule").Id.Should().Be(rule.Id);
        Blocker(await Send(new DeleteFieldRequest(note.Id, 0)), "rule").Id.Should().Be(rule.Id);
    }

    [Fact]
    public async Task Field_ReferencedByView_ListsView()
    {
        var (name, note) = await CustWithFields();
        var view = new ListView
        {
            ModuleId = await ModuleId("cust"), NameI18n = Text("清單"), Columns = [name.Id],
            Filter = J($"{{\"all\":[{{\"field\":{note.Id},\"op\":\"eq\",\"value\":\"a\"}}]}}"),
            Sort = [new ViewSort(note.Id, "asc")]
        };
        Ctx.Set<ListView>().Add(view);
        await Ctx.SaveChangesAsync();
        await Deactivate(name.Id);
        await Deactivate(note.Id);

        Blocker(await Send(new DeleteFieldRequest(name.Id, 0)), "view").Id.Should().Be(view.Id);
        Blocker(await Send(new DeleteFieldRequest(note.Id, 0)), "view").Id.Should().Be(view.Id);
    }

    [Fact]
    public async Task Field_ReferencedByReport_ListsReport()
    {
        var (name, _) = await CustWithFields();
        var def = new ReportDefinition(1, "cust", null, [new ReportFieldRef("main", name.Id, null)], [], [], [], null, null, [], "table");
        var report = new Report { OwnerUserId = "admin", Name = "客戶清單", DefinitionJson = ReportDefinition.Serialize(def), RowVersion = "x" };
        Ctx.Set<Report>().Add(report);
        await Ctx.SaveChangesAsync();
        await Deactivate(name.Id);

        Blocker(await Send(new DeleteFieldRequest(name.Id, 0)), "report").Id.Should().Be(report.Id);
    }

    [Fact]
    public async Task Field_UsedOnlyAsReportSortKey_ListsReport()
    {
        var (name, _) = await CustWithFields();
        // 欄位只出現在排序鍵（來源:欄位Id:經由欄位Id 的格式），沒有出現在欄、分組、量值或條件
        var def = new ReportDefinition(1, "cust", null, [], [], [new ReportMeasure("n", "countMain", null, null)], [], null, null,
            [new ReportSort($"main:{name.Id}:", true)], "table");
        var report = new Report { OwnerUserId = "admin", Name = "依名稱排序", DefinitionJson = ReportDefinition.Serialize(def), RowVersion = "x" };
        Ctx.Set<Report>().Add(report);
        await Ctx.SaveChangesAsync();
        await Deactivate(name.Id);

        var r = await Send(new DeleteFieldRequest(name.Id, 0));

        r.IsConflict.Should().BeTrue();
        Blocker(r, "report").Id.Should().Be(report.Id);
        (await Ctx.Set<Field>().AnyAsync(f => f.Id == name.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Field_SortKeyThatIsAMeasureId_DoesNotBlock()
    {
        var (name, _) = await CustWithFields();
        var def = new ReportDefinition(1, "cust", null, [], [], [new ReportMeasure("n", "countMain", null, null)], [], null, null,
            [new ReportSort("n", true)], "table");
        Ctx.Set<Report>().Add(new Report { OwnerUserId = "admin", Name = "依筆數排序", DefinitionJson = ReportDefinition.Serialize(def), RowVersion = "x" });
        await Ctx.SaveChangesAsync();
        await Deactivate(name.Id);

        (await Send(new DeleteFieldRequest(name.Id, 0))).IsSuccess.Should().BeTrue();
    }

    private static string LayoutSortedBy(string? sortKey) => DashboardLayout.Serialize(new DashboardLayout(
        [new DashboardCard("c1", 1, null, "bar", 0, 0, 6, 4, CardOptions.None with { SortKey = sortKey })]));

    [Fact]
    public async Task Field_UsedAsAssignedDashboardCardSortKey_ListsDashboard()
    {
        var (name, _) = await CustWithFields();
        var dashboard = new Dashboard { OwnerUserId = null, Name = "營運總覽", LayoutJson = LayoutSortedBy($"main:{name.Id}:") };
        Ctx.Set<Dashboard>().Add(dashboard);
        await Ctx.SaveChangesAsync();
        await Deactivate(name.Id);

        var r = await Send(new DeleteFieldRequest(name.Id, 0));

        r.IsConflict.Should().BeTrue();
        var b = Blocker(r, "dashboard");
        b.Id.Should().Be(dashboard.Id);
        b.Name.Should().Be("營運總覽");
    }

    [Fact]
    public async Task Field_UsedAsUsersOwnDashboardCardSortKey_ListsDashboard()
    {
        var (name, _) = await CustWithFields();
        var dashboard = new Dashboard { OwnerUserId = "sales01", Name = "我的看板", LayoutJson = LayoutSortedBy($"child:5:{name.Id}") };
        Ctx.Set<Dashboard>().Add(dashboard);
        await Ctx.SaveChangesAsync();
        await Deactivate(name.Id);

        Blocker(await Send(new DeleteFieldRequest(name.Id, 0)), "dashboard").Id.Should().Be(dashboard.Id);
    }

    [Fact]
    public async Task Field_UsedInAPersonalVersionOfAnAssignedDashboard_ListsPersonalDashboard()
    {
        var (name, _) = await CustWithFields();
        var assigned = new Dashboard { OwnerUserId = null, Name = "營運總覽", LayoutJson = LayoutSortedBy(null) };
        Ctx.Set<Dashboard>().Add(assigned);
        await Ctx.SaveChangesAsync();
        Ctx.Set<DashboardPersonalVersion>().Add(new DashboardPersonalVersion
            { DashboardId = assigned.Id, UserId = "sales01", LayoutJson = LayoutSortedBy($"main:{name.Id}:"), BaseVersion = 1 });
        await Ctx.SaveChangesAsync();
        await Deactivate(name.Id);

        var r = await Send(new DeleteFieldRequest(name.Id, 0));

        r.IsConflict.Should().BeTrue();
        Blocker(r, "personalDashboard").Id.Should().Be(assigned.Id);
        CheckOf(r).Blockers.Should().NotContain(b => b.Kind == "dashboard", "指派版本身沒有引用，只有個人版引用");
    }

    [Fact]
    public async Task Field_DashboardLayoutThatCannotBeParsed_BlocksFailClosed()
    {
        var (name, _) = await CustWithFields();
        Ctx.Set<Dashboard>().Add(new Dashboard { OwnerUserId = null, Name = "壞掉的版面", LayoutJson = "{ not json" });
        await Ctx.SaveChangesAsync();
        await Deactivate(name.Id);

        Blocker(await Send(new DeleteFieldRequest(name.Id, 0)), "dashboard");
    }

    [Fact]
    public async Task Module_FieldUsedAsDashboardCardSortKey_ListsDashboardAndKeepsModule()
    {
        await NewModule("cust");
        var name = await MustAdd("cust", FieldType.Text, new FieldConfig { IsFilterable = true }, "名稱");
        Ctx.Set<Dashboard>().Add(new Dashboard { OwnerUserId = null, Name = "營運總覽", LayoutJson = LayoutSortedBy($"main:{name.Id}:") });
        await Ctx.SaveChangesAsync();

        var r = await Send(new DeleteModuleRequest("cust", 0));

        r.IsConflict.Should().BeTrue();
        Blocker(r, "dashboard");
        (await ModuleExists("cust")).Should().BeTrue();
    }

    [Fact]
    public async Task Field_ReferencedByRollup_ListsRollup()
    {
        await NewModule("req");
        await NewModule("rec");
        var source = await MustAdd("rec", FieldType.Reference, RefTo("req"), "申請");
        var hours = await MustAdd("rec", FieldType.Decimal, new FieldConfig { Scale = 2 }, "工時");
        var roll = await MustAdd("req", FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(source.Id, RollupFunction.Sum, hours.Id, null) }, "總工時");
        await Deactivate(hours.Id);

        var r = await Send(new DeleteFieldRequest(hours.Id, 0));

        r.IsConflict.Should().BeTrue();
        Blocker(r, "rollup").Id.Should().Be(roll.Id);
    }

    [Fact]
    public async Task Field_ReferencedByChildParentDefault_ListsSource()
    {
        await NewModule("req");
        await NewModule("rec");
        var due = await MustAdd("req", FieldType.Date, new FieldConfig(), "到期日");
        await MustAdd("rec", FieldType.Reference, RefTo("req", ReferenceRelation.Detail), "申請");
        var day = await MustAdd("rec", FieldType.Date, new FieldConfig { ParentDefaultFieldId = due.Id }, "日期");
        await Deactivate(due.Id);

        var r = await Send(new DeleteFieldRequest(due.Id, 0));

        r.IsConflict.Should().BeTrue();
        Blocker(r, "parentDefault").Id.Should().Be(day.Id);
    }

    [Fact]
    public async Task Field_UnknownId_NotFound() =>
        (await Send(new DeleteFieldRequest(999999, 0))).IsNotFound.Should().BeTrue();

    // ───────────── 區塊 ─────────────

    private async Task<(long BlockId, FieldResponse Field)> SecondBlockWithField()
    {
        var added = await Send(new AddBlockRequest("cust", Text("第二區塊")));
        added.IsCreated.Should().BeTrue();
        var blockId = ((BlockResponse)added.Data!).Id;
        var f = await Send(new DynamicModules.Application.Dynamic.FieldService.Commands.AddField.AddFieldRequest(
            "cust", blockId, FieldType.Text, Text("備註"), new FieldConfig()));
        f.IsCreated.Should().BeTrue();
        return (blockId, (FieldResponse)f.Data!);
    }

    [Fact]
    public async Task Block_WithActiveField_IsBlocked_ListingTheField()
    {
        await NewModule("cust");
        var (blockId, field) = await SecondBlockWithField();

        var r = await Send(new DeleteBlockRequest(blockId, 0));

        r.IsConflict.Should().BeTrue();
        Blocker(r, "fieldActive").Id.Should().Be(field.Id);
        (await Ctx.Set<Block>().AnyAsync(b => b.Id == blockId)).Should().BeTrue();
    }

    [Fact]
    public async Task Block_WithInactiveCleanFields_IsDeletedWithItsFields()
    {
        await NewModule("cust");
        var (blockId, field) = await SecondBlockWithField();
        await Deactivate(field.Id);

        (await Send(new DeleteBlockRequest(blockId, 0))).IsSuccess.Should().BeTrue();

        (await Ctx.Set<Block>().AnyAsync(b => b.Id == blockId)).Should().BeFalse();
        (await Ctx.Set<Field>().AnyAsync(f => f.Id == field.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Block_FieldHasData_IsBlocked_AndNothingDeleted()
    {
        await NewModule("cust", activate: true);
        var (blockId, field) = await SecondBlockWithField();
        await NewRecord("cust", (field, "\"x\""));
        await Deactivate(field.Id);

        var r = await Send(new DeleteBlockRequest(blockId, 0));

        r.IsConflict.Should().BeTrue();
        Blocker(r, "fieldData").Id.Should().Be(field.Id);
        (await Ctx.Set<Field>().AnyAsync(f => f.Id == field.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Block_LastBlock_IsBlocked()
    {
        await NewModule("cust");
        var m = await Detail("cust");

        var r = await Send(new DeleteBlockRequest(m.Blocks[0].Id, 0));

        r.IsConflict.Should().BeTrue();
        Blocker(r, "lastBlock");
    }

    [Fact]
    public async Task Block_TargetedByRule_ListsRule()
    {
        await NewModule("cust");
        var (blockId, field) = await SecondBlockWithField();
        await Deactivate(field.Id);
        var m = await Detail("cust");
        var rule = new Rule
        {
            ModuleId = await ModuleId("cust"), Kind = RuleKind.Visibility,
            Condition = J($"{{\"field\":{m.TitleFieldId},\"op\":\"eq\",\"value\":\"x\"}}"), Targets = [$"block:{blockId}"]
        };
        Ctx.Set<Rule>().Add(rule);
        await Ctx.SaveChangesAsync();

        Blocker(await Send(new DeleteBlockRequest(blockId, 0)), "rule").Id.Should().Be(rule.Id);
    }

    // ───────────── 權限 ─────────────

    private async Task GrantSalesDesigner(bool edit, bool delete)
    {
        var sales = await Ctx.Set<PermissionGroup>().SingleAsync(g => g.Key == "sales");
        Ctx.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
            { GroupId = sales.Id, ProgramCode = "DYN00000", CanRead = true, CanEdit = edit, CanDelete = delete });
        await Ctx.SaveChangesAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)] // 有 Edit 沒有 Delete：仍然擋下
    public async Task WithoutDesignerDelete_AllRequestsForbidden_AndNothingDeleted(bool edit, bool delete)
    {
        await NewModule("cust");
        var m = await Detail("cust");
        var extra = await MustAdd("cust", FieldType.Text, new FieldConfig(), "備註");
        await Deactivate(extra.Id);
        if (edit || delete) await GrantSalesDesigner(edit, delete);
        As("sales01");

        (await Send(new DeleteModuleRequest("cust", 0))).IsForbidden.Should().BeTrue();
        (await Send(new DeleteFieldRequest(extra.Id, 0))).IsForbidden.Should().BeTrue();
        (await Send(new DeleteBlockRequest(m.Blocks[0].Id, 0))).IsForbidden.Should().BeTrue();
        (await Send(new CheckDeleteRequest(DeleteTarget.Module, "cust", 0))).IsForbidden.Should().BeTrue();
        (await Send(new CheckDeleteRequest(DeleteTarget.Field, null, extra.Id))).IsForbidden.Should().BeTrue();
        (await Send(new CheckDeleteRequest(DeleteTarget.Block, null, m.Blocks[0].Id))).IsForbidden.Should().BeTrue();

        (await ModuleExists("cust")).Should().BeTrue();
        (await Ctx.Set<Field>().AnyAsync(f => f.Id == extra.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task WithDesignerDelete_Allowed()
    {
        await NewModule("cust");
        await GrantSalesDesigner(edit: false, delete: true);
        As("sales01");

        (await Send(new DeleteModuleRequest("cust", 0))).IsSuccess.Should().BeTrue();
    }

    // ───────────── 交易 ─────────────

    [Fact]
    public async Task Module_FailureAtLastStep_RollsBackEveryPurge()
    {
        await NewModule("cust", activate: true);
        var name = await MustAdd("cust", FieldType.Text, new FieldConfig { IsFilterable = true }, "名稱");
        await SoftDelete(await NewRecord("cust", (name, "\"甲\"")));
        await SoftDelete(await NewRecord("cust", (name, "\"乙\"")));
        await Stop("cust");
        var records = await Ctx.Set<Record>().CountAsync();
        var index = await Ctx.Set<RecordIndex>().CountAsync();
        var history = await Ctx.Set<RecordHistory>().CountAsync();
        var program = await Ctx.Set<DynamicProgram>().CountAsync();
        // 最後刪模組本身時失敗：前面的記錄、索引、歷程清除都必須回滾
        await Ctx.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER fail_module_delete BEFORE DELETE ON Dynamic_Module BEGIN SELECT RAISE(ABORT, 'boom'); END;");

        var act = () => Send(new DeleteModuleRequest("cust", 2));

        await act.Should().ThrowAsync<Exception>();
        Ctx.ChangeTracker.Clear();
        (await ModuleExists("cust")).Should().BeTrue();
        (await Ctx.Set<Record>().CountAsync()).Should().Be(records);
        (await Ctx.Set<RecordIndex>().CountAsync()).Should().Be(index);
        (await Ctx.Set<RecordHistory>().CountAsync()).Should().Be(history);
        (await Ctx.Set<DynamicProgram>().CountAsync()).Should().Be(program);
    }

    [Fact]
    public async Task Field_FailureAtLastStep_RollsBackPurgeAndValueRewrite()
    {
        var (name, note) = await CustWithFields();
        var gone = await NewRecord("cust", (name, "\"甲\""), (note, "\"留\""));
        await SoftDelete(gone);
        await Deactivate(name.Id);
        var index = await Ctx.Set<RecordIndex>().CountAsync(i => i.FieldId == name.Id);
        var history = await Ctx.Set<RecordHistory>().CountAsync(h => h.FieldId == name.Id);
        await Ctx.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER fail_field_delete BEFORE DELETE ON Dynamic_Field BEGIN SELECT RAISE(ABORT, 'boom'); END;");

        var act = () => Send(new DeleteFieldRequest(name.Id, 1));

        await act.Should().ThrowAsync<Exception>();
        Ctx.ChangeTracker.Clear();
        (await Ctx.Set<Field>().AnyAsync(f => f.Id == name.Id)).Should().BeTrue();
        (await Ctx.Set<RecordIndex>().CountAsync(i => i.FieldId == name.Id)).Should().Be(index);
        (await Ctx.Set<RecordHistory>().CountAsync(h => h.FieldId == name.Id)).Should().Be(history);
        (await Ctx.Set<Record>().AsNoTracking().SingleAsync()).ValuesJson.Should().Contain(name.Key);
    }
}
