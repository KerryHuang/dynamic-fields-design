using System.Text.Json;
using DynamicModules.Application.Dynamic.CheckService;
using DynamicModules.Application.Dynamic.LedgerService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>跨記錄檢查定義的儲存與驗證（設計 §5.1）。兩組不相干情境：庫存異動加總不小於 0、請假天數加總不超過餘額額度。</summary>
public class CheckDefinitionTests : EffectsTestBase
{
    private static CheckInput Check(string key, string module, CheckDefinition def) => new(key, Text(key), module, true, def);

    private static CheckDefinition SumAtLeastZero(Stock s) => new([s.MoveItem, s.MoveWhere], "sum", s.MoveQty, null,
        new CheckAssert("gte", new CheckLimit(0, null, null)), Text("庫存不足"));

    [Fact]
    public async Task Stock_check_is_saved_and_listed()
    {
        var s = await BuildStock("chs");
        var r = await Send(new CreateCheckRequest(Check("chs-min", s.Move, SumAtLeastZero(s))));
        r.IsCreated.Should().BeTrue(string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        ((IReadOnlyList<CheckResponse>)(await Send(new GetChecksRequest(ModuleKey: s.Move))).Data!)
            .Should().ContainSingle(c => c.Key == "chs-min");
    }

    [Fact]
    public async Task Leave_check_uses_limit_via_reference()
    {
        var l = await BuildLeave("chl");
        var def = new CheckDefinition([l.ReqBalance], "sum", l.ReqDays, JsonDocument.Parse($$"""{"all":[{"field":{{l.ReqStatus}},"op":"eq","value":"opt_{{l.ReqApproved}}"}]}""").RootElement.Clone(),
            new CheckAssert("lte", new CheckLimit(null, l.BalLimit, l.ReqBalance)), Text("超過額度"));
        var r = await Send(new CreateCheckRequest(Check("chl-cap", l.Req, def)));
        r.IsCreated.Should().BeTrue(string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
    }

    [Fact]
    public async Task Group_key_cannot_be_a_decimal_field()
    {
        var s = await BuildStock("chd");
        var bad = SumAtLeastZero(s) with { GroupBy = [s.MoveQty] };
        var r = await Send(new CreateCheckRequest(Check("chd-a", s.Move, bad)));
        r.IsInvalid.Should().BeTrue();
        r.Errors.Keys.Should().Contain("definition.groupBy");
    }

    [Fact]
    public async Task Sum_field_must_be_numeric_and_belong_to_the_module()
    {
        var s = await BuildStock("chn");
        (await Send(new CreateCheckRequest(Check("chn-a", s.Move, SumAtLeastZero(s) with { FieldId = s.MoveItem })))).IsInvalid.Should().BeTrue();
        (await Send(new CreateCheckRequest(Check("chn-b", s.Move, SumAtLeastZero(s) with { FieldId = s.LineQty })))).IsInvalid.Should().BeTrue();
    }

    [Fact]
    public async Task Where_must_parse_against_the_module()
    {
        var s = await BuildStock("chw");
        var r = await Send(new CreateCheckRequest(Check("chw-a", s.Move, SumAtLeastZero(s) with
        {
            Where = JsonDocument.Parse("""{"all":[{"field":999999,"op":"eq","value":"x"}]}""").RootElement.Clone()
        })));
        r.IsInvalid.Should().BeTrue();
        r.Errors.Keys.Should().Contain("definition.where");
    }

    [Fact]
    public async Task Limit_must_be_exactly_one_of_const_field_via_and_via_must_be_a_reference()
    {
        var l = await BuildLeave("chv");
        var none = new CheckDefinition([l.ReqBalance], "sum", l.ReqDays, null, new CheckAssert("lte", new CheckLimit(null, null, null)), Text("x"));
        (await Send(new CreateCheckRequest(Check("chv-a", l.Req, none)))).IsInvalid.Should().BeTrue();
        var notRef = none with { Assert = new CheckAssert("lte", new CheckLimit(null, l.BalLimit, l.ReqDays)) };
        (await Send(new CreateCheckRequest(Check("chv-b", l.Req, notRef)))).IsInvalid.Should().BeTrue();
    }

    [Fact]
    public async Task Message_is_required_and_count_needs_no_field()
    {
        var s = await BuildStock("chm");
        var noMessage = SumAtLeastZero(s) with { Message = new I18nText() };
        (await Send(new CreateCheckRequest(Check("chm-a", s.Move, noMessage)))).IsInvalid.Should().BeTrue();
        var count = SumAtLeastZero(s) with { Function = "count", FieldId = null, Assert = new CheckAssert("lte", new CheckLimit(1, null, null)) };
        (await Send(new CreateCheckRequest(Check("chm-b", s.Move, count)))).IsCreated.Should().BeTrue();
    }

    [Fact]
    public async Task Update_and_delete_work_and_key_cannot_change()
    {
        var s = await BuildStock("chu");
        (await Send(new CreateCheckRequest(Check("chu-a", s.Move, SumAtLeastZero(s))))).IsCreated.Should().BeTrue();
        (await Send(new UpdateCheckRequest("chu-a", Check("chu-a", s.Move, SumAtLeastZero(s) with { Message = Text("不夠") })))).IsSuccess.Should().BeTrue();
        (await Send(new UpdateCheckRequest("chu-a", Check("chu-z", s.Move, SumAtLeastZero(s))))).IsInvalid.Should().BeTrue();
        (await Send(new DeleteCheckRequest("chu-a"))).IsSuccess.Should().BeTrue();
        ((IReadOnlyList<CheckResponse>)(await Send(new GetChecksRequest())).Data!).Should().BeEmpty();
    }
}

/// <summary>分錄設定的儲存與驗證（設計 §6.1、§10.1、§10.2）。</summary>
public class LedgerDefinitionTests : EffectsTestBase
{
    [Fact]
    public async Task Ledger_on_an_empty_module_is_saved()
    {
        var s = await BuildStock("lds");
        var r = await Send(new SaveLedgerRequest(s.Move, new LedgerInput(s.MoveReversal, [s.MoveQty], [], false)));
        r.IsSuccess.Should().BeTrue(string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        ((LedgerResponse)(await Send(new GetLedgerRequest(s.Move))).Data!).ReversalFieldId.Should().Be(s.MoveReversal);
    }

    [Fact]
    public async Task Reversal_field_must_be_a_readonly_self_reference()
    {
        var s = await BuildStock("ldr");
        var notSelf = await Send(new SaveLedgerRequest(s.Move, new LedgerInput(s.MoveSource, [s.MoveQty], [], false)));
        notSelf.IsInvalid.Should().BeTrue();
        notSelf.Errors.Keys.Should().Contain("reversalFieldId");
    }

    [Fact]
    public async Task Module_with_a_workflow_cannot_be_a_ledger()
    {
        var s = await BuildStock("ldw");
        // 單據有狀態機，但也沒有自我參照欄位；先給它一個自我參照再驗狀態機
        var self = await MustAdd(s.Doc, FieldType.Reference, RefTo(s.Doc) with { ReadOnly = true }, "沖銷自");
        var r = await Send(new SaveLedgerRequest(s.Doc, new LedgerInput(self.Id, [], [], false)));
        r.IsInvalid.Should().BeTrue();
        r.Errors.Keys.Should().Contain("moduleKey");
    }

    [Fact]
    public async Task Negate_fields_must_be_numeric_and_swap_pairs_must_match_type()
    {
        var s = await BuildStock("ldn");
        (await Send(new SaveLedgerRequest(s.Move, new LedgerInput(s.MoveReversal, [s.MoveItem], [], false)))).IsInvalid.Should().BeTrue();
        (await Send(new SaveLedgerRequest(s.Move, new LedgerInput(s.MoveReversal, [], [[s.MoveQty, s.MoveItem]], false)))).IsInvalid.Should().BeTrue();
    }

    [Fact]
    public async Task Existing_records_require_confirm_freeze()
    {
        var s = await BuildStock("ldf");
        await CreateRecord(s.Move);
        var blocked = await Send(new SaveLedgerRequest(s.Move, new LedgerInput(s.MoveReversal, [s.MoveQty], [], false)));
        blocked.IsConflict.Should().BeTrue();
        blocked.Message.Should().Contain("1");
        (await Send(new SaveLedgerRequest(s.Move, new LedgerInput(s.MoveReversal, [s.MoveQty], [], true)))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Ledger_with_records_cannot_be_removed_but_an_empty_one_can()
    {
        var s = await BuildStock("ldx");
        (await Send(new SaveLedgerRequest(s.Move, new LedgerInput(s.MoveReversal, [s.MoveQty], [], false)))).IsSuccess.Should().BeTrue();
        (await Send(new DeleteLedgerRequest(s.Move))).IsSuccess.Should().BeTrue();

        await CreateRecord(s.Move);
        (await Send(new SaveLedgerRequest(s.Move, new LedgerInput(s.MoveReversal, [s.MoveQty], [], true)))).IsSuccess.Should().BeTrue();
        (await Send(new DeleteLedgerRequest(s.Move))).IsConflict.Should().BeTrue();
    }

    [Fact]
    public async Task Ordinary_references_in_a_ledger_must_restrict_but_detail_to_header_is_exempt()
    {
        // 傳票味：表頭與借貸明細都設為分錄
        await NewModule("ldj-head");
        var rev = await MustAdd("ldj-head", FieldType.Reference, RefTo("ldj-head") with { ReadOnly = true }, "沖銷自");
        var amt = await MustAdd("ldj-head", FieldType.Decimal, label: "金額");
        await NewModule("ldj-line");
        await MustAdd("ldj-line", FieldType.Reference, RefTo("ldj-head", ReferenceRelation.Detail), "表頭");
        await Send(new DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule.ActivateModuleRequest("ldj-line"));
        await Send(new DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule.ActivateModuleRequest("ldj-head"));
        var ok = await Send(new SaveLedgerRequest("ldj-head", new LedgerInput(rev.Id, [amt.Id], [], false)));
        ok.IsSuccess.Should().BeTrue(string.Join(";", ok.Errors.Select(e => e.Key + e.Value[0])));

        // 一般參照被設成 setNull 的模組不能設為分錄
        await NewModule("ldj-other");
        var rev2 = await MustAdd("ldj-other", FieldType.Reference, RefTo("ldj-other") with { ReadOnly = true }, "沖銷自");
        await MustAdd("ldj-other", FieldType.Reference, RefTo("ldj-head", onDelete: ReferenceOnDelete.SetNull), "參照");
        await Send(new DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule.ActivateModuleRequest("ldj-other"));
        var bad = await Send(new SaveLedgerRequest("ldj-other", new LedgerInput(rev2.Id, [], [], false)));
        bad.IsInvalid.Should().BeTrue();
    }
}
