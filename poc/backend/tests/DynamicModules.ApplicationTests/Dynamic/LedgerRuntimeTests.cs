using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.LedgerService;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>分錄的執行期行為（設計 §6.2、§6.3、§6.4）。兩組不相干情境：庫存異動（數量取負號）、傳票（借貸互換、明細取負號）。</summary>
public class LedgerRuntimeTests : EffectsTestBase
{
    private static Dictionary<string, JsonElement?> Vals(params (long Field, string Json)[] v) =>
        v.ToDictionary(x => K(x.Field), x => (JsonElement?)J(x.Json));

    private async Task<DynamicRecordResponse> Add(string module, Dictionary<string, JsonElement?> values,
        Dictionary<string, List<DetailRowInput>>? details = null)
    {
        var r = await Send(new CreateRecordRequest(module, values, Details: details));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (DynamicRecordResponse)r.Data!;
    }

    private async Task<Stock> StockLedger(string p)
    {
        var s = await BuildStock(p);
        (await Send(new SaveLedgerRequest(s.Move, new LedgerInput(s.MoveReversal, [s.MoveQty], [], false)))).IsSuccess.Should().BeTrue();
        return s;
    }

    private Dictionary<string, JsonElement?> Move(Stock s, string title, string item, string qty) =>
        Vals((s.MoveTitle, $"\"{title}\""), (s.MoveItem, $"\"{item}\""), (s.MoveQty, qty));

    // ---- 不可修改 ----

    [Fact]
    public async Task Editing_or_deleting_a_ledger_record_is_a_conflict_but_creating_is_fine()
    {
        var s = await StockLedger("lrm");
        var rec = await Add(s.Move, Move(s, "m1", "A", "5"));

        var edit = await Send(new UpdateRecordRequest(rec.Id, Vals((s.MoveQty, "9")), rec.RowVersion));
        edit.IsConflict.Should().BeTrue();
        edit.Message.Should().Contain("分錄不可修改");
        (await Send(new DeleteRecordRequest(rec.Id, rec.RowVersion))).IsConflict.Should().BeTrue();

        var stored = Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking().Single(r => r.Id == rec.Id);
        stored.DeleteFlag.Should().BeFalse();
        stored.RowVersion.Should().Be(rec.RowVersion);
    }

    [Fact]
    public async Task Schema_of_a_ledger_module_flags_it_and_hides_edit_and_delete()
    {
        var s = await StockLedger("lrs");
        var schema = (DynamicModules.Application.Dynamic.SchemaService.Data.ModuleSchemaResponse)
            (await Send(new DynamicModules.Application.Dynamic.SchemaService.Queries.GetModuleSchema.GetModuleSchemaRequest(s.Move))).Data!;
        schema.Ledger.Should().BeTrue();
        schema.Permissions.Edit.Should().BeFalse();
        schema.Permissions.Delete.Should().BeFalse();
        schema.Permissions.Add.Should().BeTrue();
    }

    // ---- 沖銷：庫存味 ----

    [Fact]
    public async Task Reversal_negates_the_numbers_links_to_the_original_and_leaves_the_original_untouched()
    {
        var s = await StockLedger("lrr");
        var original = await Add(s.Move, Move(s, "m1", "A", "5"));

        var r = await Send(new ReverseRecordRequest(original.Id, null));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        var reversal = (DynamicRecordResponse)r.Data!;
        reversal.Values[K(s.MoveQty)]!.Value.GetDecimal().Should().Be(-5);
        reversal.Values[K(s.MoveItem)]!.Value.GetString().Should().Be("A");
        reversal.Values[K(s.MoveReversal)].Should().NotBeNull();

        var stored = Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking().Single(x => x.Id == original.Id);
        stored.RowVersion.Should().Be(original.RowVersion, "原分錄不動");

        var info = (RecordLedgerResponse)(await Send(new GetRecordLedgerRequest(original.Id))).Data!;
        info.Reversible.Should().BeFalse();
        info.ReversedBy!.Id.Should().Be(reversal.Id);
        var back = (RecordLedgerResponse)(await Send(new GetRecordLedgerRequest(reversal.Id))).Data!;
        back.Reverses!.Id.Should().Be(original.Id);
        back.Reversible.Should().BeFalse("沖銷分錄本身不能再被沖銷");
    }

    [Fact]
    public async Task A_record_cannot_be_reversed_twice_and_a_reversal_cannot_be_reversed()
    {
        var s = await StockLedger("lrd");
        var original = await Add(s.Move, Move(s, "m1", "A", "5"));
        var reversal = (DynamicRecordResponse)(await Send(new ReverseRecordRequest(original.Id, null))).Data!;

        (await Send(new ReverseRecordRequest(original.Id, null))).IsConflict.Should().BeTrue();
        (await Send(new ReverseRecordRequest(reversal.Id, null))).IsConflict.Should().BeTrue();
        Db.Context.Set<LedgerReversal>().Count().Should().Be(1);
    }

    [Fact]
    public async Task Only_ledger_records_can_be_reversed()
    {
        var s = await BuildStock("lro");
        var plain = await Add(s.Move, Move(s, "m1", "A", "5"));
        (await Send(new ReverseRecordRequest(plain.Id, null))).IsConflict.Should().BeTrue();
    }

    [Fact]
    public async Task Reversal_field_cannot_be_written_directly()
    {
        var s = await StockLedger("lrf");
        var original = await Add(s.Move, Move(s, "m1", "A", "5"));
        var forged = Move(s, "m2", "A", "1");
        forged[K(s.MoveReversal)] = J(original.Id.ToString());
        var r = await Send(new CreateRecordRequest(s.Move, forged));
        // 唯讀欄位的輸入被忽略，新增得到的是一般分錄，沒有沖銷關係
        if (r.IsCreated) ((DynamicRecordResponse)r.Data!).Values.GetValueOrDefault(K(s.MoveReversal)).Should().BeNull();
        Db.Context.Set<LedgerReversal>().Count().Should().Be(0);
    }

    // ---- 沖銷覆寫規則 ----

    [Fact]
    public async Task Descriptive_fields_may_be_overridden_but_numbers_and_reversal_fields_may_not()
    {
        var s = await StockLedger("lrv");
        var original = await Add(s.Move, Move(s, "m1", "A", "5"));

        var numeric = await Send(new ReverseRecordRequest(original.Id, Vals((s.MoveQty, "0"))));
        numeric.IsInvalid.Should().BeTrue();
        numeric.Errors.Keys.Should().Contain($"values.{K(s.MoveQty)}");
        var reversalField = await Send(new ReverseRecordRequest(original.Id, Vals((s.MoveReversal, "1"))));
        reversalField.IsInvalid.Should().BeTrue();
        Db.Context.Set<LedgerReversal>().Count().Should().Be(0, "失敗的沖銷不佔用沖銷資格");

        var ok = await Send(new ReverseRecordRequest(original.Id, Vals((s.MoveWhere, "\"更正倉\""))));
        ok.IsCreated.Should().BeTrue(ok.Message + string.Join(";", ok.Errors.Select(e => e.Key + e.Value[0])));
        ((DynamicRecordResponse)ok.Data!).Values[K(s.MoveWhere)]!.Value.GetString().Should().Be("更正倉");
    }

    [Fact]
    public async Task Fields_used_by_checks_cannot_be_overridden()
    {
        var s = await StockLedger("lrk");
        var def = new DynamicModules.Domain.Entities.Dynamic.CheckDefinition([s.MoveWhere], "sum", s.MoveQty, null,
            new CheckAssert("gte", new CheckLimit(-1000, null, null)), Text("不足"));
        (await Send(new DynamicModules.Application.Dynamic.CheckService.CreateCheckRequest(
            new DynamicModules.Application.Dynamic.CheckService.CheckInput("lrk-c", Text("c"), s.Move, true, def)))).IsCreated.Should().BeTrue();
        var original = await Add(s.Move, Move(s, "m1", "A", "5"));
        var r = await Send(new ReverseRecordRequest(original.Id, Vals((s.MoveWhere, "\"X\""))));
        r.IsInvalid.Should().BeTrue();
        r.Errors.Keys.Should().Contain($"values.{K(s.MoveWhere)}");
    }

    // ---- 沖銷：傳票味（借貸互換、明細取負號） ----

    [Fact]
    public async Task Voucher_reversal_swaps_debit_and_credit_and_negates_the_lines()
    {
        await NewModule("lrj-head");
        var rev = await MustAdd("lrj-head", FieldType.Reference, RefTo("lrj-head") with { ReadOnly = true }, "沖銷自");
        var debit = await MustAdd("lrj-head", FieldType.Decimal, label: "借");
        var credit = await MustAdd("lrj-head", FieldType.Decimal, label: "貸");
        var title = (await Detail("lrj-head")).TitleFieldId!.Value;
        await NewModule("lrj-line");
        await MustAdd("lrj-line", FieldType.Reference, RefTo("lrj-head", ReferenceRelation.Detail), "表頭");
        var account = await MustAdd("lrj-line", FieldType.Text, label: "科目");
        var amount = await MustAdd("lrj-line", FieldType.Decimal, label: "金額");
        await Send(new ActivateModuleRequest("lrj-line"));
        await Send(new ActivateModuleRequest("lrj-head"));
        (await Send(new SaveLedgerRequest("lrj-head", new LedgerInput(rev.Id, [amount.Id], [[debit.Id, credit.Id]], false)))).IsSuccess
            .Should().BeTrue();

        var lineTitle = (await Detail("lrj-line")).TitleFieldId!.Value;
        var original = await Add("lrj-head", Vals((title, "\"v1\""), (debit.Id, "100"), (credit.Id, "40")), new()
        {
            ["lrj-line"] =
            [
                new DetailRowInput(null, Vals((lineTitle, "\"l1\""), (account.Id, "\"現金\""), (amount.Id, "100")), null),
                new DetailRowInput(null, Vals((lineTitle, "\"l2\""), (account.Id, "\"收入\""), (amount.Id, "-60")), null)
            ]
        });

        var r = await Send(new ReverseRecordRequest(original.Id, null));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        var reversal = (DynamicRecordResponse)r.Data!;
        reversal.Values[K(debit.Id)]!.Value.GetDecimal().Should().Be(40);
        reversal.Values[K(credit.Id)]!.Value.GetDecimal().Should().Be(100);
        var lines = reversal.Details!["lrj-line"].Select(l => l.Values[K(amount.Id)]!.Value.GetDecimal()).OrderBy(x => x).ToList();
        lines.Should().Equal(-100m, 60m);

        // 明細也不可改
        var line = Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking()
            .First(x => x.ModuleId != reversal.Id && x.Title == "l1");
        (await Send(new UpdateRecordRequest(line.Id, Vals((amount.Id, "1")), line.RowVersion))).IsSuccess.Should().BeFalse();
    }

    // ---- 沖銷自檢：公式欄位 ----

    [Fact]
    public async Task Formula_fields_are_recomputed_not_compared_in_the_self_check()
    {
        await NewModule("lrx");
        var rev = await MustAdd("lrx", FieldType.Reference, RefTo("lrx") with { ReadOnly = true }, "沖銷自");
        var qty = await MustAdd("lrx", FieldType.Decimal, label: "數量");
        var price = await MustAdd("lrx", FieldType.Decimal, label: "單價");
        var amount = await MustAdd("lrx", FieldType.Formula,
            new FieldConfig { Formula = $"{{{qty.Id}}}*{{{price.Id}}}", ResultType = "number" }, "金額");
        await Send(new ActivateModuleRequest("lrx"));
        (await Send(new SaveLedgerRequest("lrx", new LedgerInput(rev.Id, [qty.Id], [], false)))).IsSuccess.Should().BeTrue();
        var title = (await Detail("lrx")).TitleFieldId!.Value;

        var original = await Add("lrx", Vals((title, "\"x1\""), (qty.Id, "2"), (price.Id, "10")));
        original.Values[K(amount.Id)]!.Value.GetDecimal().Should().Be(20);

        var r = await Send(new ReverseRecordRequest(original.Id, null));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        var reversal = (DynamicRecordResponse)r.Data!;
        reversal.Values[K(qty.Id)]!.Value.GetDecimal().Should().Be(-2);
        reversal.Values[K(amount.Id)]!.Value.GetDecimal().Should().Be(-20, "公式照公式重算：−2 × 10");
    }

    // ---- 後盾 ----

    [Fact]
    public async Task Backstop_rejects_changes_to_existing_ledger_records_made_by_any_path()
    {
        var s = await StockLedger("lrb");
        var original = await Add(s.Move, Move(s, "m1", "A", "5"));
        var journal = Svc<IWriteJournal>();
        var cursor = journal.Entries.Count;
        // 模擬某條寫入路徑（彙總、連帶清空…）改寫了本請求之前就存在的分錄：直接寫一筆 Modified 日誌項目（另一個記錄 id），再跑後盾階段
        var existing = journal.Entries[^1];
        journal.Add(seq => existing with { Seq = seq, RecordId = existing.RecordId + 1000, Kind = JournalKind.Modified, BeforeValues = existing.AfterValues, AfterValues = "{}" });

        var stage = Svc<LedgerBackstopStage>();
        var failure = await stage.RunAsync(default);
        failure.Should().NotBeNull();
        failure!.IsConflict.Should().BeTrue();
        failure.Message.Should().Contain("分錄");

        journal.Entries.Count.Should().BeGreaterThan(cursor);
    }

    [Fact]
    public async Task Backstop_allows_records_added_in_this_request()
    {
        var s = await StockLedger("lra");
        await Add(s.Move, Move(s, "m1", "A", "5"));
        (await Svc<LedgerBackstopStage>().RunAsync(default)).Should().BeNull();
    }

    // ---- 設計器：既有分錄資料不隨定義改寫 ----

    [Fact]
    public async Task Designer_changes_that_would_rewrite_ledger_values_are_blocked_once_records_exist()
    {
        var s = await StockLedger("lrg");
        // 沒記錄時放行
        (await AddField(s.Move, FieldType.Formula, new FieldConfig { Formula = $"{{{s.MoveQty}}}*2", ResultType = "number" }, "兩倍")).IsCreated.Should().BeTrue();

        await Add(s.Move, Move(s, "m1", "A", "5"));
        // 有記錄後：新增公式會重算寫回 → 409；新增一般欄位（舊記錄沒有值）照常放行
        var formula = await AddField(s.Move, FieldType.Formula, new FieldConfig { Formula = $"{{{s.MoveQty}}}*3", ResultType = "number" }, "三倍");
        formula.IsConflict.Should().BeTrue();
        (await AddField(s.Move, FieldType.Text, label: "備註")).IsCreated.Should().BeTrue();
    }
}
