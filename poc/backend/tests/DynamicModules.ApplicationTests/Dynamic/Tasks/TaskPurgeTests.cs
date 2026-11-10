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
}
