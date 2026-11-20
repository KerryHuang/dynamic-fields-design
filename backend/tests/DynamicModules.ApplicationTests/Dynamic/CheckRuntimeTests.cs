using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.CheckService;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>
/// 跨記錄檢查的執行期行為（設計 §5.2 到 §5.4）。兩組不相干情境：庫存異動（同品項加總不小於 0）、
/// 請假單（已核准的天數加總不超過參照餘額的額度）。
/// </summary>
public class CheckRuntimeTests : EffectsTestBase
{
    protected override bool ScopePerRequest => true;

    private static Dictionary<string, JsonElement?> Vals(params (long Field, string Json)[] v) =>
        v.ToDictionary(x => K(x.Field), x => (JsonElement?)J(x.Json));

    /// <summary>測試共用同一個 DbContext：失敗的請求回滾後，追蹤中的實體還留著失敗時的狀態；正式環境每個請求自己一個 scope。</summary>
    private async Task<ServiceResult> Run(IRequest<ServiceResult> request)
    {
        var result = await Send(request);
        if (!result.IsSuccess) Db.Context.ChangeTracker.Clear();
        return result;
    }

    private Task<ServiceResult> Create(string module, Dictionary<string, JsonElement?> values) => Run(new CreateRecordRequest(module, values));

    private async Task<DynamicRecordResponse> MustCreate(string module, Dictionary<string, JsonElement?> values)
    {
        var r = await Create(module, values);
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (DynamicRecordResponse)r.Data!;
    }

    private Dictionary<string, JsonElement?> Move(Stock s, string title, string item, string qty, string wh = "w1") =>
        Vals((s.MoveTitle, $"\"{title}\""), (s.MoveItem, $"\"{item}\""), (s.MoveQty, qty), (s.MoveWhere, $"\"{wh}\""));

    private static string Violation(ServiceResult r) => r.Errors[RecordValueProcessor.RecordErrorKey][0];

    private async Task StockCheck(Stock s, string? message = null, bool active = true)
    {
        var def = new CheckDefinition([s.MoveItem, s.MoveWhere], "sum", s.MoveQty, null,
            new CheckAssert("gte", new CheckLimit(0, null, null)), Text(message ?? "庫存不足"));
        var r = await Send(new CreateCheckRequest(new CheckInput(s.Move + "-min", Text("min"), s.Move, active, def)));
        r.IsCreated.Should().BeTrue(string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
    }

    // ---- 庫存味 ----

    [Fact]
    public async Task Stock_cannot_go_negative_per_group_and_the_failed_write_leaves_nothing()
    {
        var s = await BuildStock("crs");
        await StockCheck(s);
        await MustCreate(s.Move, Move(s, "in", "A", "10"));
        await MustCreate(s.Move, Move(s, "out", "A", "-3"));

        var over = await Create(s.Move, Move(s, "out2", "A", "-8"));
        over.IsInvalid.Should().BeTrue();
        Violation(over).Should().Be("庫存不足");

        // 別的品項、別的倉庫是各自的組
        (await Create(s.Move, Move(s, "x", "B", "-1"))).IsInvalid.Should().BeTrue();
        (await Create(s.Move, Move(s, "y", "A", "-8", "w2"))).IsInvalid.Should().BeTrue();
        (await Create(s.Move, Move(s, "z", "A", "-7"))).IsCreated.Should().BeTrue();

        Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking()
            .Count(r => r.ModuleId == Db.Context.Set<Module>().Single(m => m.Key == s.Move).Id).Should().Be(3);
    }

    [Fact]
    public async Task Editing_or_deleting_an_incoming_record_is_checked_with_its_before_state()
    {
        var s = await BuildStock("cru");
        await StockCheck(s);
        var incoming = await MustCreate(s.Move, Move(s, "in", "A", "10"));
        await MustCreate(s.Move, Move(s, "out", "A", "-8"));

        // 把進貨從 10 改成 2：這組的加總變 −6
        (await Run(new UpdateRecordRequest(incoming.Id, Vals((s.MoveQty, "2")), incoming.RowVersion))).IsInvalid.Should().BeTrue();
        // 刪掉進貨：同樣
        (await Run(new DeleteRecordRequest(incoming.Id, incoming.RowVersion))).IsInvalid.Should().BeTrue();
        // 改成 9 仍然 ≥ 0
        (await Run(new UpdateRecordRequest(incoming.Id, Vals((s.MoveQty, "9")), incoming.RowVersion))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Moving_a_record_to_another_group_checks_the_group_it_left()
    {
        var s = await BuildStock("crg");
        await StockCheck(s);
        var incoming = await MustCreate(s.Move, Move(s, "in", "A", "10"));
        await MustCreate(s.Move, Move(s, "out", "A", "-8"));
        // 進貨改記到 B：B 變 +10 沒問題，但 A 只剩 −8
        var r = await Run(new UpdateRecordRequest(incoming.Id, Vals((s.MoveItem, "\"B\"")), incoming.RowVersion));
        r.IsInvalid.Should().BeTrue();
    }

    [Fact]
    public async Task Exact_sums_do_not_drift_like_floating_point()
    {
        var s = await BuildStock("cre");
        var def = new CheckDefinition([s.MoveItem], "sum", s.MoveQty, null,
            new CheckAssert("lte", new CheckLimit(null, null, null) with { Const = 0.3m }), Text("超過"));
        (await Send(new CreateCheckRequest(new CheckInput("cre-cap", Text("cap"), s.Move, true, def)))).IsCreated.Should().BeTrue();
        await MustCreate(s.Move, Move(s, "a", "A", "0.1"));
        // 0.1 + 0.2 的 double 加總是 0.30000000000000004，會被誤判超過 0.3
        (await Create(s.Move, Move(s, "b", "A", "0.2"))).IsCreated.Should().BeTrue();
        (await Create(s.Move, Move(s, "c", "A", "0.01"))).IsInvalid.Should().BeTrue();
    }

    [Fact]
    public async Task A_value_too_large_to_sum_exactly_is_rejected_on_write_instead_of_being_undercounted()
    {
        var s = await BuildStock("cro");
        var qty = (await Detail(s.Move)).Blocks.SelectMany(b => b.Fields).Single(f => f.Id == s.MoveQty);
        (await Update(qty, qty.Config with { Scale = 6 })).IsSuccess.Should().BeTrue();
        await StockCheck(s);

        // 1e14 × 10^6 = 1e20，超過 64 位元整數（約 9.2e18）
        var big = await Create(s.Move, Move(s, "big", "A", "100000000000000"));
        big.IsInvalid.Should().BeTrue();
        Violation(big).Should().Contain("數值太大");
        (await Create(s.Move, Move(s, "ok", "A", "100000000000"))).IsCreated.Should().BeTrue();
    }

    [Fact]
    public async Task Creating_a_check_over_a_legacy_value_with_too_many_decimals_says_so_instead_of_too_large()
    {
        var s = await BuildStock("crs");
        var qty = (await Detail(s.Move)).Blocks.SelectMany(b => b.Fields).Single(f => f.Id == s.MoveQty);
        (await Update(qty, qty.Config with { Scale = 2 })).IsSuccess.Should().BeTrue();
        await MustCreate(s.Move, Move(s, "old", "A", "1.23"));
        // 還沒被檢查引用時把小數位調低，1.23 留著
        qty = (await Detail(s.Move)).Blocks.SelectMany(b => b.Fields).Single(f => f.Id == s.MoveQty);
        (await Update(qty, qty.Config with { Scale = 1 })).IsSuccess.Should().BeTrue();

        var def = new CheckDefinition([s.MoveItem, s.MoveWhere], "sum", s.MoveQty, null,
            new CheckAssert("gte", new CheckLimit(0, null, null)), Text("庫存不足"));
        var r = await Send(new CreateCheckRequest(new CheckInput(s.Move + "-min", Text("min"), s.Move, true, def)));
        r.IsConflict.Should().BeTrue();
        r.Message.Should().Contain("小數位超過欄位設定").And.NotContain("數值太大");
    }

    [Fact]
    public async Task Count_function_and_default_message_without_numbers()
    {
        var s = await BuildStock("crc");
        var def = new CheckDefinition([s.MoveItem], "count", null, null,
            new CheckAssert("lte", new CheckLimit(2, null, null)), Text("同一品項最多兩筆"));
        (await Send(new CreateCheckRequest(new CheckInput("crc-cnt", Text("cnt"), s.Move, true, def)))).IsCreated.Should().BeTrue();
        await MustCreate(s.Move, Move(s, "a", "A", "1"));
        await MustCreate(s.Move, Move(s, "b", "A", "1"));
        var third = await Create(s.Move, Move(s, "c", "A", "1"));
        Violation(third).Should().Be("同一品項最多兩筆").And.NotContain("3");
    }

    [Fact]
    public async Task Message_placeholders_show_value_and_limit_only_when_the_designer_asks()
    {
        var s = await BuildStock("crp");
        await StockCheck(s, "目前 {value}，下限 {limit}");
        await MustCreate(s.Move, Move(s, "in", "A", "10"));
        Violation(await Create(s.Move, Move(s, "out", "A", "-12"))).Should().Be("目前 -2，下限 0");
    }

    [Fact]
    public async Task Inactive_checks_do_not_run()
    {
        var s = await BuildStock("cri");
        await StockCheck(s, active: false);
        (await Create(s.Move, Move(s, "out", "A", "-1"))).IsCreated.Should().BeTrue();
    }

    // ---- 請假味 ----

    private async Task<(DynamicRecordResponse Balance, Func<string, Task<DynamicRecordResponse>> NewReq)> LeaveSetup(Leave l)
    {
        var def = new CheckDefinition([l.ReqBalance], "sum", l.ReqDays,
            J($$"""{"all":[{"field":{{l.ReqStatus}},"op":"eq","value":"opt_{{l.ReqApproved}}"}]}"""),
            new CheckAssert("lte", new CheckLimit(null, l.BalLimit, l.ReqBalance)), Text("超過額度"));
        var r = await Send(new CreateCheckRequest(new CheckInput(l.Req + "-cap", Text("cap"), l.Req, true, def)));
        r.IsCreated.Should().BeTrue(string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        var balance = await MustCreate(l.Balance, Vals((l.BalName, "\"b1\""), (l.BalLimit, "5")));
        var title = (await Detail(l.Req)).TitleFieldId!.Value;
        return (balance, days => MustCreate(l.Req, Vals((title, "\"r\""), (l.ReqBalance, balance.Id.ToString()), (l.ReqDays, days))));
    }

    private Task<ServiceResult> Approve(Leave l, DynamicRecordResponse req) =>
        Run(new UpdateRecordRequest(req.Id, Vals((l.ReqStatus, $"\"opt_{l.ReqApproved}\"")), req.RowVersion));

    [Fact]
    public async Task Leave_total_is_limited_by_the_referenced_balance_and_only_approved_requests_count()
    {
        var l = await BuildLeave("crl");
        var (_, newReq) = await LeaveSetup(l);
        var a = await newReq("3");
        var b = await newReq("3");
        var c = await newReq("9");          // 草稿不算，建立不被擋
        (await Approve(l, a)).IsSuccess.Should().BeTrue();
        var denied = await Approve(l, b);   // 3 + 3 > 5
        denied.IsInvalid.Should().BeTrue();
        Violation(denied).Should().Be("超過額度");
        (await Approve(l, c)).IsInvalid.Should().BeTrue();
    }

    [Fact]
    public async Task Leave_without_a_resolvable_limit_fails_closed()
    {
        var l = await BuildLeave("crm");
        var (_, newReq) = await LeaveSetup(l);
        var orphan = await MustCreate(l.Req, Vals(((await Detail(l.Req)).TitleFieldId!.Value, "\"o\""), (l.ReqDays, "1")));   // 沒有選餘額
        var r = await Approve(l, orphan);
        r.IsInvalid.Should().BeTrue();
        Violation(r).Should().Contain("找不到上限");
    }

    [Fact]
    public async Task Where_state_before_the_write_is_evaluated_too()
    {
        // 只算倉庫 w1：把 w1 的進貨改記到 w2，這筆「現在不符合 where」，但 w1 這組的加總因此變負，必須看寫入前的狀態才抓得到
        var s = await BuildStock("crb");
        var def = new CheckDefinition([s.MoveItem], "sum", s.MoveQty, J($$"""{"all":[{"field":{{s.MoveWhere}},"op":"eq","value":"w1"}]}"""),
            new CheckAssert("gte", new CheckLimit(0, null, null)), Text("庫存不足"));
        (await Send(new CreateCheckRequest(new CheckInput("crb-min", Text("min"), s.Move, true, def)))).IsCreated.Should().BeTrue();
        var incoming = await MustCreate(s.Move, Move(s, "in", "A", "10"));
        await MustCreate(s.Move, Move(s, "out", "A", "-8"));

        (await Run(new UpdateRecordRequest(incoming.Id, Vals((s.MoveWhere, "\"w2\"")), incoming.RowVersion))).IsInvalid.Should().BeTrue();
        // 別的倉庫的負數不在 where 內，不算
        (await Create(s.Move, Move(s, "x", "A", "-5", "w2"))).IsCreated.Should().BeTrue();
    }

    // ---- 索引：自動可篩選、回填、鎖定 ----

    [Fact]
    public async Task Saving_a_check_turns_filterable_on_and_backfills_existing_records()
    {
        await NewModule("crf");
        var item = await MustAdd("crf", FieldType.Text, label: "品項");        // 不可篩選
        var qty = await MustAdd("crf", FieldType.Decimal, label: "數量");      // 不可篩選
        await Send(new DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule.ActivateModuleRequest("crf"));
        var title = (await Detail("crf")).TitleFieldId!.Value;
        await MustCreate("crf", Vals((title, "\"a\""), (item.Id, "\"A\""), (qty.Id, "5")));
        await MustCreate("crf", Vals((title, "\"b\""), (item.Id, "\"A\""), (qty.Id, "5")));

        var def = new CheckDefinition([item.Id], "sum", qty.Id, null, new CheckAssert("lte", new CheckLimit(10, null, null)), Text("太多"));
        var saved = await Send(new CreateCheckRequest(new CheckInput("crf-cap", Text("cap"), "crf", true, def)));
        saved.IsCreated.Should().BeTrue(string.Join(";", saved.Errors.Select(e => e.Key + e.Value[0])));
        ((CheckResponse)saved.Data!).Notices.Should().NotBeEmpty();

        var fields = (await Detail("crf")).Blocks.SelectMany(b => b.Fields).ToList();
        fields.Single(f => f.Id == item.Id).Config.IsFilterable.Should().BeTrue();
        fields.Single(f => f.Id == qty.Id).Config.IsFilterable.Should().BeTrue();

        // 既有的 5 + 5 = 10 已回填進索引：再加 1 就超過
        (await Create("crf", Vals((title, "\"c\""), (item.Id, "\"A\""), (qty.Id, "1")))).IsInvalid.Should().BeTrue();
        (await Create("crf", Vals((title, "\"d\""), (item.Id, "\"B\""), (qty.Id, "1")))).IsCreated.Should().BeTrue();
    }

    [Fact]
    public async Task Fields_used_by_a_check_cannot_lose_their_index_or_change_scale()
    {
        var s = await BuildStock("crk");
        await StockCheck(s);
        var qty = (await Detail(s.Move)).Blocks.SelectMany(b => b.Fields).Single(f => f.Id == s.MoveQty);

        var scale = await Update(qty, qty.Config with { Scale = 4 });
        scale.IsConflict.Should().BeTrue();
        scale.Message.Should().Contain(s.Move + "-min");

        var unindexed = await Update(qty, qty.Config with { IsFilterable = false });
        unindexed.IsConflict.Should().BeTrue();

        // 與檢查無關的設定照常可改
        (await Update(qty, qty.Config with { Required = true })).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Scan_reports_groups_that_already_violate_without_blocking()
    {
        var s = await BuildStock("crn");
        await MustCreate(s.Move, Move(s, "out", "A", "-4"));
        await MustCreate(s.Move, Move(s, "ok", "B", "4"));
        await StockCheck(s);   // 新增檢查不回頭掃描、也不擋

        var scan = (CheckScanResponse)(await Send(new ScanCheckRequest(s.Move + "-min"))).Data!;
        scan.Violations.Should().ContainSingle();
        scan.Violations[0].Value.Should().Be(-4m);
        scan.Truncated.Should().BeFalse();
    }

    // ---- 政策 A28、A29：試跑回報組數、寫入後驗證組數 ----

    private async Task SetPolicy(PolicyDefinition def, int value) =>
        (await Send(new UpdateSystemPolicyRequest(def.Code, JsonSerializer.SerializeToElement(value)))).IsSuccess.Should().BeTrue();

    [Fact]
    public async Task Scan_ViolationPolicy()
    {
        var s = await BuildStock("crv");
        await MustCreate(s.Move, Move(s, "a", "A", "-4"));
        await MustCreate(s.Move, Move(s, "b", "B", "-2"));
        await StockCheck(s);
        await SetPolicy(PolicyCatalog.CheckScanMaxViolations, 1);

        var scan = (CheckScanResponse)(await Send(new ScanCheckRequest(s.Move + "-min"))).Data!;
        scan.Violations.Should().ContainSingle();
        scan.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task Scan_GroupPolicy_TightenedTruncates()
    {
        var s = await BuildStock("crq");
        await StockCheck(s);
        var moduleId = Db.Context.Set<Module>().AsNoTracking().Single(m => m.Key == s.Move).Id;
        // 技術下限 1000：直接寫 1001 筆各自一組的記錄（品項都不同），不走建檔流程。
        // 第 1、1000、1001 組各放一筆違規（數量 -1、-2、-3），用違規數證明實際掃到幾組，空集合不會通過
        Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AddRange(Enumerable.Range(0, 1001).Select(i =>
        {
            var rec = new DynamicModules.Domain.Entities.Dynamic.Record
            {
                ModuleId = moduleId, OwnerUserId = "admin", RowVersion = Guid.NewGuid().ToString(),
                ValuesJson = JsonSerializer.Serialize(Vals((s.MoveItem, $"\"I{i}\""), (s.MoveQty, i switch { 0 => "-1", 999 => "-2", 1000 => "-3", _ => "1" }), (s.MoveWhere, "\"w1\"")))
            };
            rec.StampCreated("admin", new DateTime(2026, 11, 3, 9, 0, 0));
            return rec;
        }));
        await Db.Context.SaveChangesAsync();
        // 直接寫表不經建檔流程，檢查的聚合讀索引，索引要自己補；沒補的話每組都聚合成 0，違規永遠掃不出來
        var withFields = (await Svc<IModuleRepository>().GetByKeyAsync(s.Move, true, CancellationToken.None))!;
        foreach (var record in Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().Where(r => r.ModuleId == moduleId).ToList())
        {
            var rows = RecordIndexer.Build(withFields, RecordValueProcessor.Parse(record.ValuesJson), Svc<FieldTypeRegistry>());
            foreach (var row in rows) row.RecordId = record.Id;
            Db.Context.AddRange(rows);
        }
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();

        var full = (CheckScanResponse)(await Send(new ScanCheckRequest(s.Move + "-min"))).Data!;
        full.Violations.Select(v => v.Value).Should().Equal([-1m, -2m, -3m], "出廠 20000 組，1001 組全掃到");
        full.Truncated.Should().BeFalse("出廠 20000 組，1001 組掃得完");

        await SetPolicy(PolicyCatalog.CheckScanMaxGroups, 1000);
        var cut = (CheckScanResponse)(await Send(new ScanCheckRequest(s.Move + "-min"))).Data!;
        cut.Violations.Select(v => v.Value).Should().Equal([-1m, -2m], "只掃前 1000 組：第 1000 組掃到、第 1001 組沒掃");
        cut.Truncated.Should().BeTrue("調嚴到 1000 組，第 1000 組就截斷");
    }

    /// <summary>A29 的組數是整個請求累計：表頭與明細各一組、各自不超過 1，加起來 2 就擋。</summary>
    [Fact]
    public async Task Write_GroupPolicy_CountsAcrossModules()
    {
        await NewModule("crx-head");
        var kind = await MustAdd("crx-head", FieldType.Text, new FieldConfig { IsFilterable = true }, "類別");
        var amount = await MustAdd("crx-head", FieldType.Decimal, new FieldConfig { IsFilterable = true }, "金額");
        await NewModule("crx-line");
        await MustAdd("crx-line", FieldType.Reference, RefTo("crx-head", ReferenceRelation.Detail), "表頭");
        var item = await MustAdd("crx-line", FieldType.Text, new FieldConfig { IsFilterable = true }, "品項");
        var qty = await MustAdd("crx-line", FieldType.Decimal, new FieldConfig { IsFilterable = true }, "數量");
        (await Send(new ActivateModuleRequest("crx-line"))).IsSuccess.Should().BeTrue();
        (await Send(new ActivateModuleRequest("crx-head"))).IsSuccess.Should().BeTrue();
        foreach (var (module, group, sum) in new[] { ("crx-head", kind.Id, amount.Id), ("crx-line", item.Id, qty.Id) })
        {
            var def = new CheckDefinition([group], "sum", sum, null, new CheckAssert("gte", new CheckLimit(0, null, null)), Text("不足"));
            var created = await Send(new CreateCheckRequest(new CheckInput(module + "-min", Text("min"), module, true, def)));
            created.IsCreated.Should().BeTrue(string.Join(";", created.Errors.Select(e => e.Key + e.Value[0])));
        }
        var headTitle = (await Detail("crx-head")).TitleFieldId!.Value;
        var lineTitle = (await Detail("crx-line")).TitleFieldId!.Value;
        CreateRecordRequest Request() => new("crx-head", Vals((headTitle, "\"h\""), (kind.Id, "\"A\""), (amount.Id, "5")),
            Details: new() { ["crx-line"] = [new DetailRowInput(null, Vals((lineTitle, "\"l\""), (item.Id, "\"X\""), (qty.Id, "3")), null)] });
        await SetPolicy(PolicyCatalog.CheckMaxGroupsPerWrite, 1);

        var r = await Run(Request());
        r.IsConflict.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        r.Message.Should().Be("這次請求牽涉的檢查組數超過 1，請分批處理");
        var ids = Db.Context.Set<Module>().AsNoTracking().Where(m => m.Key == "crx-head" || m.Key == "crx-line").Select(m => m.Id).ToList();
        Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking().Count(x => ids.Contains(x.ModuleId))
            .Should().Be(0, "被擋的寫入整筆回滾");

        await SetPolicy(PolicyCatalog.CheckMaxGroupsPerWrite, 2);
        (await Run(Request())).IsCreated.Should().BeTrue();
    }

    [Fact]
    public async Task Write_GroupPolicy()
    {
        var s = await BuildStock("crw");
        await StockCheck(s);
        var incoming = await MustCreate(s.Move, Move(s, "in", "A", "10"));
        await SetPolicy(PolicyCatalog.CheckMaxGroupsPerWrite, 1);

        // 品項 A 改成 B：寫入前的 A 組與寫入後的 B 組都要驗
        var r = await Run(new UpdateRecordRequest(incoming.Id, Vals((s.MoveItem, "\"B\"")), incoming.RowVersion));
        r.IsConflict.Should().BeTrue();
        r.Message.Should().Be("這次請求牽涉的檢查組數超過 1，請分批處理");
        // 只牽涉一組的寫入照常
        (await Create(s.Move, Move(s, "in2", "A", "1"))).IsCreated.Should().BeTrue();
    }
}
