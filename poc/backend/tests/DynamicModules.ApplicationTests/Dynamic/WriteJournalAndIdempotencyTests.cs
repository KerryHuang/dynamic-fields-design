using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>寫入日誌（設計 §3.3）與請求層冪等（§3.2）。</summary>
public class WriteJournalAndIdempotencyTests : DesignerTestBase
{
    private async Task<(string Module, string Title)> Module(string key)
    {
        await NewModule(key, activate: true);
        return (key, $"f_{(await Detail(key)).TitleFieldId}");
    }

    private Task<ServiceResult> Create(string module, string titleKey, string title) =>
        Send(new CreateRecordRequest(module, new Dictionary<string, JsonElement?> { [titleKey] = J($"\"{title}\"") }));

    private int OperationRows() => Db.Context.Set<OperationRecord>().AsNoTracking().Count();

    private int Records(string module) =>
        Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking().Count(r => r.ModuleId == Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Module>().First(m => m.Key == module).Id);

    private static string Title(string valuesJson, string key) => JsonDocument.Parse(valuesJson).RootElement.GetProperty(key).GetString()!;

    // ---- 寫入日誌 ----

    [Fact]
    public async Task Journal_records_added_modified_and_soft_deleted_records_in_order()
    {
        var (m, t) = await Module("jr-a");
        var journal = Svc<IWriteJournal>();
        journal.Entries.Should().BeEmpty();

        var created = (DynamicRecordResponse)(await Create(m, t, "第一")).Data!;
        journal.Entries.Should().ContainSingle();
        var added = journal.Entries[0];
        added.Kind.Should().Be(JournalKind.Added);
        added.RecordId.Should().Be(created.Id);
        added.BeforeValues.Should().BeNull();
        Title(added.AfterValues, t).Should().Be("第一");

        (await Send(new UpdateRecordRequest(created.Id, new Dictionary<string, JsonElement?> { [t] = J("\"第二\"") }, created.RowVersion))).IsSuccess.Should().BeTrue();
        var modified = journal.Entries[^1];
        modified.Kind.Should().Be(JournalKind.Modified);
        Title(modified.BeforeValues!, t).Should().Be("第一");
        Title(modified.AfterValues, t).Should().Be("第二");
        modified.Seq.Should().BeGreaterThan(added.Seq);

        var current = (DynamicRecordResponse)(await Send(new DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord.GetRecordRequest(created.Id))).Data!;
        (await Send(new DeleteRecordRequest(created.Id, current.RowVersion))).IsSuccess.Should().BeTrue();
        var deleted = journal.Entries[^1];
        deleted.BeforeDeleted.Should().BeFalse();
        deleted.AfterDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task Journal_merge_takes_first_before_and_last_after_per_record()
    {
        var (m, t) = await Module("jr-b");
        var journal = Svc<IWriteJournal>();
        var r = (DynamicRecordResponse)(await Create(m, t, "a")).Data!;
        await Send(new UpdateRecordRequest(r.Id, new Dictionary<string, JsonElement?> { [t] = J("\"b\"") }, r.RowVersion));

        var merged = JournalView.Merge(journal.Entries);
        var one = merged.Should().ContainSingle().Subject;
        one.Kind.Should().Be(JournalKind.Added, "種類以第一項為準");
        one.BeforeValues.Should().BeNull();
        Title(one.AfterValues, t).Should().Be("b");
    }

    [Fact]
    public async Task Journal_slices_by_cursor_so_each_round_sees_only_its_own_increments()
    {
        var (m, t) = await Module("jr-c");
        var journal = Svc<IWriteJournal>();
        var r = (DynamicRecordResponse)(await Create(m, t, "a")).Data!;
        var cursor = journal.Entries.Count;
        await Send(new UpdateRecordRequest(r.Id, new Dictionary<string, JsonElement?> { [t] = J("\"b\"") }, r.RowVersion));

        var round1 = JournalView.Merge(journal.Entries.Skip(cursor));
        var item = round1.Should().ContainSingle().Subject;
        item.Kind.Should().Be(JournalKind.Modified, "跨輪不合併：這一輪看到的是修改，不是新增");
        Title(item.BeforeValues!, t).Should().Be("a");
    }

    // ---- 冪等 ----

    [Fact]
    public async Task Without_a_key_each_request_runs_and_nothing_is_stored()
    {
        var (m, t) = await Module("id-a");
        (await Create(m, t, "x")).IsCreated.Should().BeTrue();
        (await Create(m, t, "x")).IsCreated.Should().BeTrue();
        Records(m).Should().Be(2);
        OperationRows().Should().Be(0);
        Svc<IOperationContext>().OperationId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Same_key_and_same_request_replays_the_first_result_without_writing_again()
    {
        var (m, t) = await Module("id-b");
        Svc<IOperationContext>().ClientKey = "key-AAAA-0001";
        var first = await Create(m, t, "x");
        var second = await Create(m, t, "x");

        first.IsCreated.Should().BeTrue();
        second.IsCreated.Should().BeTrue("回放要保留 201 的語意");
        Records(m).Should().Be(1);
        OperationRows().Should().Be(1);
        Svc<IOperationContext>().Replayed.Should().BeTrue();
        var firstId = ((DynamicRecordResponse)first.Data!).Id;
        ((JsonElement)second.Data!).GetProperty("id").GetInt64().Should().Be(firstId);
    }

    [Fact]
    public async Task Same_key_with_different_content_is_unprocessable()
    {
        var (m, t) = await Module("id-c");
        Svc<IOperationContext>().ClientKey = "key-AAAA-0002";
        (await Create(m, t, "x")).IsCreated.Should().BeTrue();
        var other = await Create(m, t, "y");
        other.IsUnprocessable.Should().BeTrue();
        Records(m).Should().Be(1);
    }

    [Fact]
    public async Task Failed_requests_leave_no_trace_so_a_resend_runs_again()
    {
        var (m, t) = await Module("id-d");
        Svc<IOperationContext>().ClientKey = "key-AAAA-0003";
        var bad = await Send(new CreateRecordRequest(m, new Dictionary<string, JsonElement?>()));
        bad.IsInvalid.Should().BeTrue();
        OperationRows().Should().Be(0);

        // 同一把 key 之後帶正確內容可以成功（不是 422）
        (await Create(m, t, "x")).IsCreated.Should().BeTrue();
        OperationRows().Should().Be(1);
    }

    [Fact]
    public async Task Keys_are_isolated_per_user()
    {
        var (m, t) = await Module("id-e");
        // 別的使用者用過同一把 key（內容不同）：對我來說這把 key 還沒用過，不會 422 也不會回放
        Db.Context.Set<OperationRecord>().Add(new OperationRecord
        {
            UserId = "someone-else", Key = "key-AAAA-0004", RequestHash = "other", ResultKind = 1, ResultJson = "{}",
            CreatedTime = new DateTime(2026, 11, 4, 9, 0, 0)
        });
        await Db.Context.SaveChangesAsync();

        Svc<IOperationContext>().ClientKey = "key-AAAA-0004";
        var mine = await Create(m, t, "x");
        mine.IsCreated.Should().BeTrue();
        Svc<IOperationContext>().Replayed.Should().BeFalse();
        Records(m).Should().Be(1);
        OperationRows().Should().Be(2);
    }

    [Fact]
    public async Task Old_operation_rows_are_purged_when_a_new_one_is_written()
    {
        var (m, t) = await Module("id-f");
        Db.Context.Set<OperationRecord>().Add(new OperationRecord
        {
            UserId = "admin", Key = "key-OLD-00001", RequestHash = "h", ResultKind = 1, ResultJson = "{}",
            CreatedTime = new DateTime(2026, 11, 4, 9, 0, 0).AddDays(-8)
        });
        await Db.Context.SaveChangesAsync();

        Svc<IOperationContext>().ClientKey = "key-AAAA-0005";
        await Create(m, t, "x");
        Db.Context.Set<OperationRecord>().AsNoTracking().Select(o => o.Key).ToList().Should().BeEquivalentTo(["key-AAAA-0005"]);
    }
}
