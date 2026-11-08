using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.CheckService;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
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
    }
}
