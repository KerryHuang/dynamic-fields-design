using DynamicModules.Application.Dynamic.DeleteService;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

public class TaskPurgeTests : DesignerTestBase
{
    [Fact]
    public async Task RemovingModule_DeletesItsTasksCommentsParticipantsAndNotifications()
    {
        await NewModule("projects", activate: true);
        await CreateRecord("projects");
        var module = await Db.Context.Set<Module>().AsNoTracking().SingleAsync(m => m.Key == "projects");
        var record = await Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking().SingleAsync(r => r.ModuleId == module.Id);
        var now = new DateTime(2026, 11, 4, 1, 0, 0, DateTimeKind.Utc);
        var task = new TaskItem
        {
            Title = "準備資料", ModuleId = module.Id, RecordId = record.Id, CreatedBy = "admin", CreatedTime = now,
            Participants = [new TaskParticipant { UserId = "admin", Reason = TaskParticipantReason.Creator }],
            Comments =
            [
                new TaskComment
                {
                    Kind = TaskCommentKind.Comment, AuthorUserId = "admin", Body = "附上檔案", CreatedTime = now,
                    Attachments = [new TaskCommentAttachment { FileName = "a.png", ContentType = "image/png", Size = 1, StorageKey = "k1", CreatedTime = now }]
                }
            ]
        };
        Db.Context.Add(task);
        await Db.Context.SaveChangesAsync();
        Db.Context.Add(new Notification
        {
            UserId = "admin", Kind = NotificationKinds.TaskAssigned, TitleSnapshot = "準備資料",
            Link = $"/tasks?open={task.Id}", SourceType = "task", SourceId = task.Id, CreatedTime = now
        });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();

        await Svc<ModuleRemover>().RemoveAsync(module.Id, default);

        (await Db.Context.Set<TaskItem>().CountAsync()).Should().Be(0);
        (await Db.Context.Set<TaskComment>().CountAsync()).Should().Be(0);
        (await Db.Context.Set<TaskCommentAttachment>().CountAsync()).Should().Be(0);
        (await Db.Context.Set<TaskParticipant>().CountAsync()).Should().Be(0);
        (await Db.Context.Set<Notification>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RemovingModule_LeavesOtherModulesAndPersonalTasks()
    {
        await NewModule("projects", activate: true);
        await NewModule("others", activate: true);
        await CreateRecord("projects");
        await CreateRecord("others");
        var modules = await Db.Context.Set<Module>().AsNoTracking().Where(m => m.Key == "projects" || m.Key == "others").ToListAsync();
        var gone = modules.Single(m => m.Key == "projects");
        var kept = modules.Single(m => m.Key == "others");
        var records = await Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking().ToListAsync();
        var now = new DateTime(2026, 11, 4, 1, 0, 0, DateTimeKind.Utc);

        TaskItem NewTask(string title, Module? m) => new()
        {
            Title = title, ModuleId = m?.Id, RecordId = m is null ? null : records.Single(r => r.ModuleId == m.Id).Id,
            CreatedBy = "admin", CreatedTime = now,
            Participants = [new TaskParticipant { UserId = "admin", Reason = TaskParticipantReason.Creator }],
            Comments = [new TaskComment { Kind = TaskCommentKind.Comment, AuthorUserId = "admin", Body = title, CreatedTime = now }]
        };
        var doomed = NewTask("要刪的", gone);
        var other = NewTask("別的模組", kept);
        var personal = NewTask("個人", null);
        Db.Context.AddRange(doomed, other, personal);
        await Db.Context.SaveChangesAsync();
        Notification Note(long sourceId, string sourceType) => new()
        {
            UserId = "admin", Kind = NotificationKinds.TaskAssigned, TitleSnapshot = "x", Link = $"/tasks?open={sourceId}",
            SourceType = sourceType, SourceId = sourceId, CreatedTime = now
        };
        // 最後一則：SourceId 與要刪的任務相同，但不是任務通知
        Db.Context.AddRange(Note(doomed.Id, "task"), Note(other.Id, "task"), Note(doomed.Id, "other"));
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();

        await Svc<ModuleRemover>().RemoveAsync(gone.Id, default);

        (await Db.Context.Set<TaskItem>().Select(t => t.Title).ToListAsync()).Should().BeEquivalentTo("別的模組", "個人");
        (await Db.Context.Set<TaskComment>().Select(c => c.Body).ToListAsync()).Should().BeEquivalentTo("別的模組", "個人");
        (await Db.Context.Set<TaskParticipant>().CountAsync()).Should().Be(2);
        (await Db.Context.Set<Notification>().Select(n => n.SourceType + ":" + n.SourceId).ToListAsync())
            .Should().BeEquivalentTo($"task:{other.Id}", $"other:{doomed.Id}");
    }
}
