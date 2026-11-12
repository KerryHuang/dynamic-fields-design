using System.Text;
using DynamicModules.Application.Common.Uploads;
using DynamicModules.Application.Dynamic.AttachmentService;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.ApplicationTests.Dynamic.Tasks;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Attachments;

/// <summary>伺服器端信任上傳範圍（Demo 產生器用）：環境停用上傳時，範圍內可寫、範圍外照舊 409；權限與副檔名照常檢查。</summary>
public class ServerUploadScopeTests : TaskTestBase
{
    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton(new AttachmentSettings(Path.Combine(Path.GetTempPath(), $"dm-scope-{Guid.NewGuid():N}"), false, "Demo 站停用上傳"));

    private async Task<(long Record, long Field)> RecordWithFileField()
    {
        await NewModule("scope");
        var field = await MustAdd("scope", FieldType.Attachment, label: "附件");
        (await Send(new ActivateModuleRequest("scope"))).IsSuccess.Should().BeTrue();
        var m = await Detail("scope");
        As("admin");
        var r = await Send(new CreateRecordRequest("scope",
            new Dictionary<string, System.Text.Json.JsonElement?> { [$"f_{m.TitleFieldId}"] = J("\"甲\"") }));
        r.IsCreated.Should().BeTrue(r.Message);
        return (((DynamicRecordResponse)r.Data!).Id, field.Id);
    }

    private static MemoryStream Bytes(string s) => new(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task Upload_Disabled_OutsideScope_409()
    {
        var (record, field) = await RecordWithFileField();

        var r = await Send(new UploadAttachmentRequest(record, field, Bytes("x"), "a.txt", 1));

        r.IsConflict.Should().BeTrue();
        r.Message.Should().StartWith(AttachmentRules.UploadDisabledPrefix);
    }

    [Fact]
    public async Task Upload_Disabled_InsideScope_Succeeds_ExtensionStillChecked()
    {
        var (record, field) = await RecordWithFileField();

        using (ServerUploadScope.Begin())
        {
            (await Send(new UploadAttachmentRequest(record, field, Bytes("x"), "a.txt", 1))).IsCreated.Should().BeTrue();
            var bad = await Send(new UploadAttachmentRequest(record, field, Bytes("x"), "a.exe", 1));
            bad.IsInvalid.Should().BeTrue("範圍只略過環境開關，副檔名白名單照常");
        }
        (await Send(new UploadAttachmentRequest(record, field, Bytes("x"), "b.txt", 1))).IsConflict.Should().BeTrue("離開範圍後恢復");
    }

    [Fact]
    public async Task Comment_Disabled_InsideScope_Succeeds()
    {
        var task = await SeedTask();
        var files = new[] { new CommentFileUpload(Bytes("png"), "a.png", "image/png") };

        (await Send(new AddTaskCommentRequest(task.Id, "附檔", files))).IsConflict.Should().BeTrue();
        using (ServerUploadScope.Begin())
            (await Send(new AddTaskCommentRequest(task.Id, "附檔", [new CommentFileUpload(Bytes("png"), "a.png", "image/png")]))).IsCreated.Should().BeTrue();
    }

    [Fact]
    public async Task Scope_RestoresAfterDispose_AndDoesNotFlowToOtherTasks()
    {
        ServerUploadScope.Active.Should().BeFalse();
        using (ServerUploadScope.Begin())
        {
            ServerUploadScope.Active.Should().BeTrue();
            using (ServerUploadScope.Begin()) ServerUploadScope.Active.Should().BeTrue();
            ServerUploadScope.Active.Should().BeTrue("巢狀 Dispose 還原成外層的值");
        }
        ServerUploadScope.Active.Should().BeFalse();

        bool seen;
        using (ServerUploadScope.Begin())
        {
            // 範圍開著時另起、不在其內建立的執行緒流程（模擬並行的 HTTP 請求）：用 UnsafeStart 切斷 ExecutionContext
            var t = new TaskCompletionSource<bool>();
            ThreadPool.UnsafeQueueUserWorkItem(_ => t.SetResult(ServerUploadScope.Active), null);
            seen = await t.Task;
        }
        seen.Should().BeFalse("沒有流過 ExecutionContext 的工作讀不到範圍");
        (await Task.Run(() => ServerUploadScope.Active)).Should().BeFalse();
    }
}
