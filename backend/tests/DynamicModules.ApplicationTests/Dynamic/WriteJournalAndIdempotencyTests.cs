using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
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
    public async Task Journal_Clear_forgets_entries_and_silent_marks_so_the_next_request_starts_clean()
    {
        var (m, t) = await Module("jr-clear");
        var journal = Svc<IWriteJournal>();
        (await Create(m, t, "A")).IsSuccess.Should().BeTrue();
        var marked = new object();
        journal.MarkSilent(marked);
        journal.Entries.Should().NotBeEmpty();

        journal.Clear();

        journal.Entries.Should().BeEmpty();
        journal.IsMarkedSilent(marked).Should().BeFalse();
        (await Create(m, t, "B")).IsSuccess.Should().BeTrue();
        journal.Entries.Should().ContainSingle("只剩清掉之後那次寫入");
    }

    [Fact]
    public async Task EffectBudget_Reset_gives_the_full_allowance_again()
    {
        var budget = Svc<DynamicModules.Application.Dynamic.EventService.EffectBudget>();
        var max = int.Parse(DynamicModules.Domain.Policies.PolicyCatalog.EventMaxWritesPerRequest.Factory);
        for (var i = 0; i < max; i++) (await budget.TryUseAsync(default)).Should().BeTrue();
        (await budget.TryUseAsync(default)).Should().BeFalse();

        budget.Reset();

        (await budget.TryUseAsync(default)).Should().BeTrue();
    }

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

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Purge_FollowsRetentionPolicy()
    {
        var (m, t) = await Module("id-g");
        (await Send(new UpdateSystemPolicyRequest(PolicyCatalog.MaintenanceIdempotencyRetentionDays.Code, JsonSerializer.SerializeToElement(1))))
            .IsSuccess.Should().BeTrue();
        var now = new DateTime(2026, 11, 4, 9, 0, 0);
        Db.Context.Set<OperationRecord>().AddRange(
            new OperationRecord { UserId = "admin", Key = "key-OLD-00002", RequestHash = "h", ResultKind = 1, ResultJson = "{}", CreatedTime = now.AddDays(-2) },
            new OperationRecord { UserId = "admin", Key = "key-NEW-00001", RequestHash = "h", ResultKind = 1, ResultJson = "{}", CreatedTime = now.AddHours(-12) });
        await Db.Context.SaveChangesAsync();

        Svc<IOperationContext>().ClientKey = "key-AAAA-0006";
        await Create(m, t, "x");
        Db.Context.Set<OperationRecord>().AsNoTracking().Select(o => o.Key).ToList()
            .Should().BeEquivalentTo(["key-NEW-00001", "key-AAAA-0006"]);
    }

    // ---- 任務與留言的冪等（mcp2a 判斷 5：IIdempotentRequest 把任務與留言納入；帶 key 才冪等） ----

    private Task<ServiceResult> NewTask(string title) => Send(new CreateTaskRequest(new TaskInput(title, null, null, null)));

    private static long IdOf(ServiceResult r) =>
        JsonSerializer.SerializeToElement(r.Data, r.Data!.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("id").GetInt64();

    private int Tasks(string title) => Db.Context.Set<TaskItem>().AsNoTracking().Count(t => t.Title == title);

    private int Comments(long taskId) =>
        Db.Context.Set<TaskComment>().AsNoTracking().Count(c => c.TaskId == taskId && c.Kind == TaskCommentKind.Comment);

    [Fact]
    public async Task Task_create_with_same_key_replays()
    {
        Svc<IOperationContext>().ClientKey = "key-TASK-0001";
        var first = await NewTask("冪等任務");
        var second = await NewTask("冪等任務");

        first.IsCreated.Should().BeTrue(first.Message);
        second.IsCreated.Should().BeTrue("回放保留 201 的語意");
        Svc<IOperationContext>().Replayed.Should().BeTrue();
        Tasks("冪等任務").Should().Be(1);
        ((JsonElement)second.Data!).GetProperty("id").GetInt64().Should().Be(IdOf(first));
    }

    [Fact]
    public async Task Task_comment_with_same_key_replays()
    {
        var taskId = IdOf(await NewTask("留言任務"));
        Svc<IOperationContext>().ClientKey = "key-TASK-0002";

        (await Send(new AddTaskCommentRequest(taskId, "第一則", []))).IsSuccess.Should().BeTrue();
        (await Send(new AddTaskCommentRequest(taskId, "第一則", []))).IsSuccess.Should().BeTrue();

        Svc<IOperationContext>().Replayed.Should().BeTrue();
        Comments(taskId).Should().Be(1);
    }

    [Fact]
    public async Task Task_update_same_key_different_patch_is_unprocessable()
    {
        var taskId = IdOf(await NewTask("改任務"));
        Svc<IOperationContext>().ClientKey = "key-TASK-0003";

        (await Send(new UpdateTaskRequest(taskId, J("""{"status":"inProgress"}""")))).IsSuccess.Should().BeTrue();
        var other = await Send(new UpdateTaskRequest(taskId, J("""{"status":"done"}""")));

        other.IsUnprocessable.Should().BeTrue();
        Db.Context.Set<TaskItem>().AsNoTracking().Single(t => t.Id == taskId).Status.Should().Be(TaskItemStatus.InProgress);
    }

    [Fact]
    public async Task Task_request_without_key_runs_every_time()
    {
        (await NewTask("沒帶鍵")).IsCreated.Should().BeTrue();
        (await NewTask("沒帶鍵")).IsCreated.Should().BeTrue();

        Tasks("沒帶鍵").Should().Be(2);
        OperationRows().Should().Be(0);
    }

    [Fact]
    public async Task Task_comment_with_files_bypasses_idempotency()
    {
        var taskId = IdOf(await NewTask("附件留言"));
        Svc<IOperationContext>().ClientKey = "key-TASK-0004";
        static IReadOnlyList<CommentFileUpload> Files() => [new CommentFileUpload(new MemoryStream([1, 2, 3]), "a.png", "image/png")];

        (await Send(new AddTaskCommentRequest(taskId, "附件", Files()))).IsSuccess.Should().BeTrue();
        (await Send(new AddTaskCommentRequest(taskId, "附件", Files()))).IsSuccess.Should().BeTrue();

        Comments(taskId).Should().Be(2, "附件留言不做請求層冪等（Stream 不能可靠雜湊）");
        Svc<IOperationContext>().Replayed.Should().BeFalse();
        OperationRows().Should().Be(0);
    }
}
