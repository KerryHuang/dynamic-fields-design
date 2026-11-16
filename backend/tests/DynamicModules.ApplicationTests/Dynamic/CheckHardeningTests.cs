using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.CheckService;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>跨記錄檢查的漏洞回歸（第一輪實作審查 b）：舊索引的小數位、本筆上限被改、組鍵碰撞、大小寫、可篩選、fieldRef 誤判。</summary>
public class CheckHardeningTests : EffectsTestBase
{
    protected override bool ScopePerRequest => true;

    private static Dictionary<string, JsonElement?> Vals(params (long Field, string Json)[] v) =>
        v.ToDictionary(x => K(x.Field), x => (JsonElement?)J(x.Json));

    private async Task<ServiceResult> Run(IRequest<ServiceResult> request)
    {
        var result = await Send(request);
        if (!result.IsSuccess) Db.Context.ChangeTracker.Clear();
        return result;
    }

    private async Task<DynamicRecordResponse> MustCreate(string module, Dictionary<string, JsonElement?> values)
    {
        var r = await Run(new CreateRecordRequest(module, values));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (DynamicRecordResponse)r.Data!;
    }

    private Dictionary<string, JsonElement?> Move(Stock s, string item, string qty, string wh = "w1") =>
        Vals((s.MoveTitle, "\"t\""), (s.MoveItem, J(JsonSerializer.Serialize(item)).GetRawText()), (s.MoveQty, qty), (s.MoveWhere, J(JsonSerializer.Serialize(wh)).GetRawText()));

    private static CheckDefinition SumGte0(List<long> groupBy, long sumField, JsonElement? where = null, CheckLimit? limit = null, string op = "gte") =>
        new(groupBy, "sum", sumField, where, new CheckAssert(op, limit ?? new CheckLimit(0, null, null)), new I18nText([KeyValuePair.Create("zh-TW", "不足")]));

    private async Task<ServiceResult> SaveCheck(Stock s, string key, CheckDefinition def) =>
        await Send(new CreateCheckRequest(new CheckInput(key, Text(key), s.Move, true, def)));

    [Fact]
    public async Task Rebuilding_a_check_after_changing_the_scale_backfills_the_stale_index()
    {
        var s = await BuildStock("chs");
        await MustCreate(s.Move, Move(s, "A", "1"));   // Scale 2：索引的 ScaledValue 是 100
        (await SaveCheck(s, "chs-min", SumGte0([s.MoveItem], s.MoveQty))).IsCreated.Should().BeTrue();
        (await Send(new DeleteCheckRequest("chs-min"))).IsSuccess.Should().BeTrue();

        var qty = (await Detail(s.Move)).Blocks.SelectMany(b => b.Fields).Single(f => f.Id == s.MoveQty);
        (await Update(qty, qty.Config with { Scale = 1 })).IsSuccess.Should().BeTrue();
        (await SaveCheck(s, "chs-min", SumGte0([s.MoveItem], s.MoveQty))).IsCreated.Should().BeTrue();   // 重建要回填

        // 真值 1 − 2 = −1；舊索引沒回填會算成 (100 − 20) / 10 = 8 而放行
        (await Run(new CreateRecordRequest(s.Move, Move(s, "A", "-2")))).IsInvalid.Should().BeTrue();
    }

    [Fact]
    public async Task A_stale_index_with_a_huge_scaled_value_is_still_detected_and_backfilled()
    {
        var s = await BuildStock("chb");
        var qty = (await Detail(s.Move)).Blocks.SelectMany(b => b.Fields).Single(f => f.Id == s.MoveQty);
        (await Update(qty, qty.Config with { Scale = 6 })).IsSuccess.Should().BeTrue();
        await MustCreate(s.Move, Move(s, "A", "10000000000"));   // Scale 6：ScaledValue 是 1e16，超過 double 能精確表示的範圍
        (await SaveCheck(s, "chb-min", SumGte0([s.MoveItem], s.MoveQty))).IsCreated.Should().BeTrue();
        (await Send(new DeleteCheckRequest("chb-min"))).IsSuccess.Should().BeTrue();

        qty = (await Detail(s.Move)).Blocks.SelectMany(b => b.Fields).Single(f => f.Id == s.MoveQty);
        (await Update(qty, qty.Config with { Scale = 5 })).IsSuccess.Should().BeTrue();
        (await SaveCheck(s, "chb-min", SumGte0([s.MoveItem], s.MoveQty))).IsCreated.Should().BeTrue();   // 重建要回填

        // 真值 1e10 − 2e10 = −1e10；舊索引沒回填會算成 (1e16 − 2e15) / 1e5 = +8e10 而放行
        (await Run(new CreateRecordRequest(s.Move, Move(s, "A", "-20000000000")))).IsInvalid.Should().BeTrue();
    }

    [Fact]
    public async Task A_limit_read_from_the_written_record_uses_its_new_value()
    {
        var s = await BuildStock("chl");
        var limit = await MustAdd(s.Move, FieldType.Decimal, new FieldConfig { IsFilterable = true }, "上限");
        (await SaveCheck(s, "chl-max", SumGte0([], s.MoveQty, limit: new CheckLimit(null, limit.Id, null), op: "lte"))).IsCreated.Should().BeTrue();
        var rec = await MustCreate(s.Move, Vals((s.MoveTitle, "\"t\""), (s.MoveQty, "10"), (limit.Id, "100")));

        // 把本筆上限改成 5：同組加總 10 超過新的上限
        var r = await Run(new UpdateRecordRequest(rec.Id, Vals((limit.Id, "5")), rec.RowVersion));

        r.IsInvalid.Should().BeTrue();
    }

    [Fact]
    public async Task Text_group_keys_that_look_alike_are_different_groups()
    {
        var s = await BuildStock("chk");
        (await SaveCheck(s, "chk-min", SumGte0([s.MoveItem, s.MoveWhere], s.MoveQty))).IsCreated.Should().BeTrue();
        var old = ($"A|||;{s.MoveWhere}=B", "C");
        var fresh = ("A", $"B|||;{s.MoveWhere}=C");   // 舊組鍵串接後與 old 完全相同
        await MustCreate(s.Move, Move(s, old.Item1, "1", old.Item2));
        var minus = await MustCreate(s.Move, Move(s, old.Item1, "-1", old.Item2));

        // 把 −1 移到空的新組：新組變 −1
        var r = await Run(new UpdateRecordRequest(minus.Id, Vals((s.MoveItem, JsonSerializer.Serialize(fresh.Item1)), (s.MoveWhere, JsonSerializer.Serialize(fresh.Item2))), minus.RowVersion));

        r.IsInvalid.Should().BeTrue();
    }

    [Fact]
    public async Task A_contains_condition_is_case_sensitive_like_the_rule_evaluator()
    {
        var s = await BuildStock("chc");
        var where = J($$"""{"all":[{"field":{{s.MoveItem}},"op":"contains","value":"A"}]}""");
        (await SaveCheck(s, "chc-min", SumGte0([], s.MoveQty, where))).IsCreated.Should().BeTrue();
        await MustCreate(s.Move, Move(s, "a", "10"));   // 不符合 where（區分大小寫），不屬於這個檢查

        // 符合 where 的只有 ("A", −8)，真值 −8；SQL 的 LIKE 不分大小寫會把 "a" 的 +10 算進來變成 2
        (await Run(new CreateRecordRequest(s.Move, Move(s, "A", "-8")))).IsInvalid.Should().BeTrue();
    }

    [Fact]
    public async Task A_where_on_a_sortable_only_field_enables_filterable_so_writes_keep_working()
    {
        var s = await BuildStock("chf");
        var tag = await MustAdd(s.Move, FieldType.Text, new FieldConfig { IsSortable = true }, "標籤");
        var where = J($$"""{"all":[{"field":{{tag.Id}},"op":"eq","value":"x"}]}""");
        var def = new CheckDefinition([], "count", null, where, new CheckAssert("lte", new CheckLimit(100, null, null)), new I18nText([KeyValuePair.Create("zh-TW", "太多")]));
        (await Send(new CreateCheckRequest(new CheckInput("chf-cnt", Text("cnt"), s.Move, true, def)))).IsCreated.Should().BeTrue();

        (await Detail(s.Move)).Blocks.SelectMany(b => b.Fields).Single(f => f.Id == tag.Id).Config.IsFilterable.Should().BeTrue();
        (await Run(new CreateRecordRequest(s.Move, Vals((s.MoveTitle, "\"t\""), (tag.Id, "\"x\""))))).IsCreated.Should().BeTrue();
    }

    [Fact]
    public async Task A_text_constant_that_spells_fieldRef_is_not_a_field_comparison()
    {
        var s = await BuildStock("chr");
        var where = J($$"""{"all":[{"field":{{s.MoveItem}},"op":"eq","value":"fieldRef"}]}""");
        var r = await SaveCheck(s, "chr-min", SumGte0([], s.MoveQty, where));
        r.IsCreated.Should().BeTrue(string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));

        // 真正的 fieldRef 仍然被擋
        var real = J($$$"""{"all":[{"field":{{{s.MoveQty}}},"op":"gt","value":{"fieldRef":{{{s.MoveQty}}}}}]}""");
        (await SaveCheck(s, "chr-real", SumGte0([], s.MoveQty, real))).IsInvalid.Should().BeTrue();
    }
}
