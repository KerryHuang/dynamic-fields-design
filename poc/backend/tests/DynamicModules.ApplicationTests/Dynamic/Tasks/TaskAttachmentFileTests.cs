using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.AttachmentService;
using DynamicModules.Application.Dynamic.DeleteService;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Persistence.Files;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>模組清除的附件檔（交易提交後才刪、單檔失敗不影響）與處理器層的上傳停用檢查。這個類別的環境停用了上傳。</summary>
public class TaskAttachmentFileTests : TaskTestBase, IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dm-task-att-{Guid.NewGuid():N}");
    private readonly FlakyStore _store;

    public TaskAttachmentFileTests() => _store = new FlakyStore(new LocalAttachmentStore(_root));

    /// <summary>Delete 遇到 FailKey 丟 IOException（例如 Windows 上檔案被鎖住）。</summary>
    private sealed class FlakyStore(IAttachmentStore inner) : IAttachmentStore
    {
        public string? FailKey { get; set; }
        public Task<long?> SaveAsync(string id, Stream content, long maxBytes, CancellationToken ct) => inner.SaveAsync(id, content, maxBytes, ct);
        public Stream? OpenRead(string id) => inner.OpenRead(id);
        public bool Delete(string id) => id == FailKey ? throw new IOException("檔案被鎖住") : inner.Delete(id);
        public IReadOnlyList<StoredAttachment> List() => inner.List();
    }

    public sealed record RemoveModuleProbe(long ModuleId, bool Fail) : IRequest<ServiceResult>, ITransactionalRequest;

    /// <summary>清除模組後，依 Fail 決定整筆回滾（模擬 DeleteApp 後面的模組或事後檢查失敗）或提交。</summary>
    public sealed class RemoveModuleProbeHandler(ModuleRemover remover) : IRequestHandler<RemoveModuleProbe, ServiceResult>
    {
        public async Task<ServiceResult> Handle(RemoveModuleProbe request, CancellationToken ct)
        {
            await remover.RemoveAsync(request.ModuleId, ct);
            return request.Fail ? ServiceResult.Conflict("後面的步驟失敗") : ServiceResult.Success();
        }
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(new AttachmentSettings(_root, false, null));
        services.AddSingleton(new PolicyEnvironmentDefaults(new Dictionary<string, string> { ["attachment.maxFileBytes"] = "1024" }));
        services.AddSingleton<IAttachmentStore>(_store);
        services.AddTransient<IRequestHandler<RemoveModuleProbe, ServiceResult>, RemoveModuleProbeHandler>();
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string[] DiskFiles() => Directory.Exists(_root) ? Directory.GetFiles(_root, "*", SearchOption.AllDirectories) : [];

    private static string Key(int n) => n.ToString("x32");

    /// <summary>一個模組、一筆記錄、一筆任務、一則留言帶兩個附件，兩個檔案都寫進磁碟。</summary>
    private async Task<(long ModuleId, long TaskId)> SeedModuleWithFiles()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var task = await SeedTask(target: t, recordId: rec);
        var now = SeedTime;
        Db.Context.Add(new TaskComment
        {
            TaskId = task.Id, Kind = TaskCommentKind.Comment, AuthorUserId = "admin", Body = "附檔", CreatedTime = now,
            Attachments =
            [
                new TaskCommentAttachment { FileName = "a.png", ContentType = "image/png", Size = 1, StorageKey = Key(1), CreatedTime = now },
                new TaskCommentAttachment { FileName = "b.png", ContentType = "image/png", Size = 1, StorageKey = Key(2), CreatedTime = now }
            ]
        });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        foreach (var n in new[] { 1, 2 })
            (await _store.SaveAsync(Key(n), new MemoryStream([1]), 1024, default)).Should().Be(1);
        return (t.ModuleId, task.Id);
    }

    [Fact]
    public async Task ModuleRemoval_RolledBack_KeepsFiles()
    {
        var (moduleId, taskId) = await SeedModuleWithFiles();

        var r = await Send(new RemoveModuleProbe(moduleId, Fail: true));

        r.IsConflict.Should().BeTrue();
        Db.Context.ChangeTracker.Clear();
        (await Db.Context.Set<TaskItem>().AnyAsync(x => x.Id == taskId)).Should().BeTrue("交易回滾，任務還在");
        DiskFiles().Should().HaveCount(2, "交易沒有提交，檔案不能先刪");
    }

    [Fact]
    public async Task ModuleRemoval_Committed_DeletesFiles_OneFailureOnlyLogged()
    {
        var (moduleId, taskId) = await SeedModuleWithFiles();
        _store.FailKey = Key(1);

        var r = await Send(new RemoveModuleProbe(moduleId, Fail: false));

        r.IsSuccess.Should().BeTrue();
        Db.Context.ChangeTracker.Clear();
        (await Db.Context.Set<TaskItem>().AnyAsync(x => x.Id == taskId)).Should().BeFalse("刪檔失敗不回滾");
        DiskFiles().Select(Path.GetFileName).Should().Equal(Key(1));
    }

    [Fact]
    public async Task Handler_UploadDisabled_409_NoFileNoComment()
    {
        var task = await SeedTask();

        var r = await Send(new AddTaskCommentRequest(task.Id, "附檔",
            [new CommentFileUpload(new MemoryStream([1, 2, 3]), "a.png", "image/png")]));

        r.IsConflict.Should().BeTrue();
        r.Message.Should().StartWith("此環境已停用附件上傳");
        DiskFiles().Should().BeEmpty();
        Db.Context.ChangeTracker.Clear();
        (await Db.Context.Set<TaskComment>().CountAsync(c => c.TaskId == task.Id)).Should().Be(0);
        // 只有文字的留言不受上傳開關影響
        (await Send(new AddTaskCommentRequest(task.Id, "純文字", []))).IsCreated.Should().BeTrue();
    }
}
