using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

public class TaskRecordSyncTests : TaskTestBase
{
    private async Task<string> RowVersion(long recordId)
    {
        Db.Context.ChangeTracker.Clear();
        return (await Db.Context.Set<Record>().AsNoTracking().SingleAsync(r => r.Id == recordId)).RowVersion;
    }

    private async Task Rename(Target t, long recordId, string title)
    {
        As("admin");
        var r = await Send(new UpdateRecordRequest(recordId,
            new Dictionary<string, System.Text.Json.JsonElement?> { [$"f_{t.TitleFieldId}"] = J($"\"{title}\"") }, await RowVersion(recordId)));
        r.IsSuccess.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        Db.Context.ChangeTracker.Clear();
    }

    private async Task Delete(long recordId)
    {
        As("admin");
        var r = await Send(new DeleteRecordRequest(recordId, await RowVersion(recordId)));
        r.IsSuccess.Should().BeTrue(r.Message);
        Db.Context.ChangeTracker.Clear();
    }

    private async Task<List<TaskComment>> Entries(long taskId) =>
        await Db.Context.Set<TaskComment>().AsNoTracking().Where(c => c.TaskId == taskId).OrderBy(c => c.Id).ToListAsync();

    [Fact]
    public async Task RecordTitleChange_UpdatesSnapshot()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01", "A");
        var task = await SeedTask(target: t, recordId: rec);

        await Rename(t, rec, "B");

        (await Load(task.Id)).RecordTitleSnapshot.Should().Be("B");
        (await Entries(task.Id)).Should().BeEmpty("改標題不寫系統記錄");
    }

    [Fact]
    public async Task RecordSoftDelete_CancelsOpenTasks_DoneUntouched()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var todo = await SeedTask(title: "待辦", target: t, recordId: rec);
        var doing = await SeedTask(title: "進行中", target: t, recordId: rec, status: TaskItemStatus.InProgress);
        var done = await SeedTask(title: "已完成", target: t, recordId: rec, status: TaskItemStatus.Done);
        var other = await SeedTask(title: "別的記錄", target: t, recordId: await NewRecord(t, "sales01", "案件乙"));

        await Delete(rec);

        foreach (var id in new[] { todo.Id, doing.Id })
        {
            var task = await Load(id);
            task.Status.Should().Be(TaskItemStatus.Cancelled);
            task.CancelledByRule.Should().BeFalse();
            task.ModifiedBy.Should().BeNull();
            var entry = (await Entries(id)).Should().ContainSingle().Subject;
            entry.Kind.Should().Be(TaskCommentKind.System);
            entry.SystemEvent.Should().Be(TaskSystemEvents.RecordDeleted);
            entry.AuthorUserId.Should().BeNull();
        }
        (await Load(done.Id)).Status.Should().Be(TaskItemStatus.Done);
        (await Entries(done.Id)).Should().BeEmpty();
        (await Load(other.Id)).Status.Should().Be(TaskItemStatus.Todo);
    }

    [Fact]
    public async Task CascadeDelete_CancelsChildRecordTasks()
    {
        var t = await BuildTarget("parent");
        await NewModule("child");
        var refField = await MustAdd("child", FieldType.Reference, RefTo("parent", onDelete: ReferenceOnDelete.Cascade), "上層");
        await Send(new DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule.ActivateModuleRequest("child"));
        var parent = await NewRecord(t, "sales01");
        var childModule = await Detail("child");
        var created = await Send(new CreateRecordRequest("child", new Dictionary<string, System.Text.Json.JsonElement?>
        {
            [$"f_{childModule.TitleFieldId}"] = J("\"子記錄\""), [refField.Key] = J(parent.ToString())
        }));
        created.IsCreated.Should().BeTrue(created.Message + string.Join(";", created.Errors.Select(e => e.Key + e.Value[0])));
        var child = ((DynamicRecordResponse)created.Data!).Id;
        var childTarget = t with { ModuleId = childModule.Id };
        var task = await SeedTask(target: childTarget, recordId: child);

        await Delete(parent);

        (await Db.Context.Set<Record>().AsNoTracking().SingleAsync(r => r.Id == child)).DeleteFlag.Should().BeTrue();
        (await Load(task.Id)).Status.Should().Be(TaskItemStatus.Cancelled);
        (await Entries(task.Id)).Single().SystemEvent.Should().Be(TaskSystemEvents.RecordDeleted);
    }

    [Fact]
    public async Task DeletedRecord_SnapshotOnlyForRecordReaders()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01", "A");
        var task = await SeedTask(createdBy: "sales01", target: t, recordId: rec, mentioned: ["floor01"]);
        await Rename(t, rec, "B");
        await Delete(rec);

        async Task<TaskPermissions> Eval(string who)
        {
            var loaded = await Load(task.Id);
            As(who);
            var p = await Svc<TaskAccess>().EvaluateAsync(loaded, default);
            As("admin");
            return p;
        }

        (await Eval("manager01")).Record.Should().Be(new TaskRecordRef(true, t.Key, rec, "B", true));
        var floor = await Eval("floor01");
        floor.CanSee.Should().BeTrue();
        floor.Record.Should().Be(new TaskRecordRef(false, null, null, null, false));
    }
}
