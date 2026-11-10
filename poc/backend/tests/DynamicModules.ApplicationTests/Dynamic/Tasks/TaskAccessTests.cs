using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

public class TaskAccessTests : TaskTestBase
{
    private async Task<TaskPermissions> Eval(string userId, long taskId)
    {
        var task = await Load(taskId);
        As(userId);
        var p = await Svc<TaskAccess>().EvaluateAsync(task, default);
        As("admin");
        return p;
    }

    [Fact]
    public async Task Creator_Assignee_RecordEditor_Admin_CanEdit_ReaderCannot()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var task = await SeedTask(createdBy: "floor01", assignee: "maint01", target: t, recordId: rec);

        foreach (var who in new[] { "floor01", "maint01", "sales01", "admin" })
        {
            var p = await Eval(who, task.Id);
            p.CanSee.Should().BeTrue(who);
            p.CanEdit.Should().BeTrue(who);
        }
        var reader = await Eval("manager01", task.Id);
        reader.CanSee.Should().BeTrue();
        reader.CanEdit.Should().BeFalse();
        reader.CanComment.Should().BeTrue();
        reader.Record.Should().Be(new TaskRecordRef(true, t.Key, rec, "案件甲", false));
    }

    [Fact]
    public async Task OutOfScopeRecord_TaskIsInvisible()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var task = await SeedTask(createdBy: "sales01", target: t, recordId: rec);

        var p = await Eval("sales02", task.Id);

        p.CanSee.Should().BeFalse();
        p.Record.Should().Be(new TaskRecordRef(false, null, null, null, false));
    }

    [Fact]
    public async Task Participant_WithoutRecordRead_SeesTaskNotRecord()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var task = await SeedTask(createdBy: "sales01", target: t, recordId: rec, mentioned: ["floor01"]);

        var p = await Eval("floor01", task.Id);

        p.CanSee.Should().BeTrue();
        p.CanComment.Should().BeTrue();
        p.CanEdit.Should().BeFalse();
        p.CanDelete.Should().BeFalse();
        p.Record.Should().Be(new TaskRecordRef(false, null, null, null, false));
    }

    [Fact]
    public async Task PersonalTask_OnlyParticipantsAndAdmin()
    {
        var task = await SeedTask(createdBy: "sales01");

        (await Eval("floor01", task.Id)).CanSee.Should().BeFalse();
        var admin = await Eval("admin", task.Id);
        admin.CanSee.Should().BeTrue();
        admin.CanDelete.Should().BeTrue();
        admin.Record.Should().BeNull();
        var creator = await Eval("sales01", task.Id);
        creator.CanEdit.Should().BeTrue();
        creator.CanDelete.Should().BeTrue();
    }

    [Fact]
    public async Task ClosedModule_TaskFrozen()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var task = await SeedTask(createdBy: "admin", target: t, recordId: rec);
        await Db.Context.Set<Module>().Where(m => m.Id == t.ModuleId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, ModuleStatus.Closed));

        var p = await Eval("admin", task.Id);

        p.CanSee.Should().BeTrue();
        p.Frozen.Should().BeTrue();
        p.CanEdit.Should().BeFalse();
        p.CanComment.Should().BeFalse();
        p.CanDelete.Should().BeFalse();
    }

    [Fact]
    public async Task HiddenTitleField_RecordRefWithoutTitle()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var task = await SeedTask(createdBy: "admin", target: t, recordId: rec);
        Db.Context.Add(new FieldPermission { FieldId = t.TitleFieldId, GroupId = t.SalesGroupId, Access = FieldAccess.Hidden });
        await Db.Context.SaveChangesAsync();

        var p = await Eval("sales01", task.Id);

        p.Record.Should().Be(new TaskRecordRef(true, t.Key, rec, null, false));
    }

    [Fact]
    public async Task ForRecord_OutOfScope_IsNull_InScope_CanAdd()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");

        As("sales02");
        (await Svc<TaskAccess>().ForRecordAsync(rec, default)).Should().BeNull();
        As("sales01");
        var a = await Svc<TaskAccess>().ForRecordAsync(rec, default);
        a.Should().NotBeNull();
        a!.CanAdd.Should().BeTrue();
        a.CanEditTasks.Should().BeTrue();
        As("manager01");
        (await Svc<TaskAccess>().ForRecordAsync(rec, default))!.CanEditTasks.Should().BeFalse();
    }
}
