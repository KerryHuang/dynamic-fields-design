using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>任務留言附件：上傳（與留言同一個 multipart 請求）、下載、撤回、清掃與模組清除。每個測試自己一個 TestApp，才能檢查磁碟。</summary>
public class TaskCommentAttachmentsApiTests : IDisposable
{
    private readonly TestApp _app = new();
    private HttpClient? _admin;

    public TaskCommentAttachmentsApiTests() => _app.Settings["Attachments:MaxBytes"] = "1024";

    private HttpClient Admin => _admin ??= _app.AsUser();

    public void Dispose() => _app.Dispose();

    private string[] DiskFiles() =>
        Directory.Exists(_app.AttachmentRoot) ? Directory.GetFiles(_app.AttachmentRoot, "*", SearchOption.AllDirectories) : [];

    private async Task<long> NewTask(HttpClient? c = null, string? recordPath = null)
    {
        var res = await (c ?? Admin).PostAsJsonAsync(recordPath ?? $"{Base}/tasks", new { title = "設計稿修改" });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    private static MultipartFormDataContent Form(string? body, params (string Name, byte[] Bytes)[] files)
    {
        var form = new MultipartFormDataContent();
        if (body is not null) form.Add(new StringContent(body), "body");
        foreach (var (name, bytes) in files)
        {
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "files", name);
        }
        return form;
    }

    private static (string, byte[]) File(string name, int size = 5) => (name, Enumerable.Repeat((byte)'x', size).ToArray());

    private Task<HttpResponseMessage> Post(long taskId, MultipartFormDataContent form, HttpClient? c = null) =>
        (c ?? Admin).PostAsync($"{Base}/tasks/{taskId}/comments", form);

    private async Task<JsonElement> PostOk(long taskId, MultipartFormDataContent form, HttpClient? c = null)
    {
        var res = await Post(taskId, form, c);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private static async Task<string> FilesError(HttpResponseMessage res)
    {
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        return (await JsonOf(res)).GetProperty("errors").GetProperty("files")[0].GetString()!;
    }

    private Task<HttpResponseMessage> Download(HttpClient c, long taskId, long commentId, long attachmentId) =>
        c.GetAsync($"{Base}/tasks/{taskId}/comments/{commentId}/attachments/{attachmentId}");

    [Fact]
    public async Task Comment_SixFiles_400()
    {
        var id = await NewTask();

        var res = await Post(id, Form("六個檔", File("1.png"), File("2.png"), File("3.png"), File("4.png"), File("5.png"), File("6.png")));

        (await FilesError(res)).Should().Be("一則留言最多 5 個附件");
        DiskFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Comment_ModuleOverrideMaxFiles()
    {
        var (module, id) = await NewRecordTask();
        (await Admin.PutAsJsonAsync($"{Base}/modules/{module}/policies/attachment.maxFilesPerComment", new { value = 2 })).EnsureSuccessStatusCode();

        var res = await Post(id, Form("三個檔", File("1.png"), File("2.png"), File("3.png")));

        (await FilesError(res)).Should().Contain("最多 2 個");
        DiskFiles().Should().BeEmpty();
        (await Post(id, Form("兩個檔", File("1.png"), File("2.png")))).StatusCode.Should().Be(HttpStatusCode.Created);
        // 個人任務（沒有模組）仍用全系統值 5
        var personal = await NewTask();
        (await Post(personal, Form("五個檔", File("1.png"), File("2.png"), File("3.png"), File("4.png"), File("5.png"))))
            .StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Comment_ModuleOverrideMaxFiles_BodyLimitFollowsOverride()
    {
        var (module, id) = await NewRecordTask();
        (await Admin.PutAsJsonAsync($"{Base}/modules/{module}/policies/attachment.maxFilesPerComment", new { value = 2 })).EnsureSuccessStatusCode();

        var res = await Post(id, Form("太大", File("big.png", 1024 * 2 + 64 * 1024 + 1)));

        res.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        DiskFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Comment_DisallowedExtension_400()
    {
        var id = await NewTask();

        (await FilesError(await Post(id, Form("x", File("a.png"), File("run.exe"))))).Should().Be("不允許的檔案類型");
        DiskFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Comment_FileTooLarge_400_NoOrphanFiles()
    {
        var id = await NewTask();

        var res = await Post(id, Form("x", File("small.png"), File("big.png", 2000)));

        (await FilesError(res)).Should().Be("檔案超過大小上限");
        DiskFiles().Should().BeEmpty();
        var list = await JsonOf(await Admin.GetAsync($"{Base}/tasks/{id}/comments"));
        list.EnumerateArray().Should().OnlyContain(c => c.GetProperty("kind").GetString() == "system");
    }

    [Fact]
    public async Task Comment_FilesOnly_NoText_201()
    {
        var id = await NewTask();

        var comment = await PostOk(id, Form(null, File("標註.png", 10), File("說明.pdf", 20)));

        comment.GetProperty("body").GetString().Should().Be("");
        var attachments = comment.GetProperty("attachments");
        attachments.GetArrayLength().Should().Be(2);
        attachments[0].GetProperty("fileName").GetString().Should().Be("標註.png");
        attachments[0].GetProperty("contentType").GetString().Should().Be("image/png");
        attachments[0].GetProperty("size").GetInt64().Should().Be(10);
        attachments[1].GetProperty("size").GetInt64().Should().Be(20);
        DiskFiles().Should().HaveCount(2);
        // 沒有文字也沒有檔案仍是 400
        var empty = await Post(id, Form(" "));
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(empty)).GetProperty("errors").GetProperty("body")[0].GetString().Should().Be("留言不能是空的");
    }

    [Fact]
    public async Task UploadDisabled_409()
    {
        _app.Settings["Attachments:UploadEnabled"] = "false";
        var id = await NewTask();

        var res = await Post(id, Form("x", File("a.png")));

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonOf(res)).GetProperty("message").GetString().Should().StartWith("此環境已停用附件上傳");
        DiskFiles().Should().BeEmpty();
        var list = await JsonOf(await Admin.GetAsync($"{Base}/tasks/{id}/comments"));
        list.EnumerateArray().Should().OnlyContain(c => c.GetProperty("kind").GetString() == "system", "沒有新增留言列");
        // 只有文字的留言照常可以
        (await Admin.PostAsJsonAsync($"{Base}/tasks/{id}/comments", new { body = "純文字" })).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Mentioned_CanDownload_InvisibleUser_404()
    {
        var id = await NewTask();
        var comment = await PostOk(id, Form("請 @{floor01} 看圖", File("a.png", 7)));
        var cid = comment.GetProperty("id").GetInt64();
        var aid = comment.GetProperty("attachments")[0].GetProperty("id").GetInt64();

        var ok = await Download(_app.AsUser("floor01"), id, cid, aid);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        ok.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
        ok.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        ok.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("sandbox");
        ok.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        (await ok.Content.ReadAsByteArrayAsync()).Should().HaveCount(7);

        var hidden = await Download(_app.AsUser("sales02"), id, cid, aid);
        hidden.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonOf(hidden)).GetProperty("message").GetString().Should().Be("找不到任務");
        var wrong = await Download(Admin, id, cid, aid + 100);
        wrong.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonOf(wrong)).GetProperty("message").GetString().Should().Be("找不到附件");
    }

    [Fact]
    public async Task WithdrawnComment_Download404_AttachmentsHidden()
    {
        var id = await NewTask();
        var comment = await PostOk(id, Form("x", File("a.png")));
        var cid = comment.GetProperty("id").GetInt64();
        var aid = comment.GetProperty("attachments")[0].GetProperty("id").GetInt64();

        (await Admin.PostAsync($"{Base}/tasks/{id}/comments/{cid}/withdraw", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await Download(Admin, id, cid, aid);
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonOf(res)).GetProperty("message").GetString().Should().Be("找不到附件");
        var list = await JsonOf(await Admin.GetAsync($"{Base}/tasks/{id}/comments"));
        list.EnumerateArray().Single(c => c.GetProperty("id").GetInt64() == cid).GetProperty("attachments").GetArrayLength().Should().Be(0);
        DiskFiles().Should().ContainSingle("撤回不立即刪檔，交給清掃");
    }

    [Fact]
    public async Task Sweep_KeepsLiveTaskAttachments_RemovesWithdrawnAfterGrace()
    {
        var id = await NewTask();
        await PostOk(id, Form("留著", File("keep.png")));
        var gone = await PostOk(id, Form("撤回", File("gone.png")));
        (await Admin.PostAsync($"{Base}/tasks/{id}/comments/{gone.GetProperty("id").GetInt64()}/withdraw", null)).EnsureSuccessStatusCode();
        DiskFiles().Should().HaveCount(2);

        var swept = await JsonOf(await Admin.PostAsync($"{Base}/attachments/sweep?graceMinutes=0", null));

        swept.GetProperty("deleted").GetInt32().Should().Be(1);
        DiskFiles().Should().ContainSingle();
        var list = await JsonOf(await Admin.GetAsync($"{Base}/tasks/{id}/comments"));
        var kept = list.EnumerateArray().Single(c => c.TryGetProperty("body", out var b) && b.GetString() == "留著");
        var res = await Download(Admin, id, kept.GetProperty("id").GetInt64(), kept.GetProperty("attachments")[0].GetProperty("id").GetInt64());
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ModuleRemoval_DeletesTaskAttachmentFiles()
    {
        var module = await CreateModule(Admin);
        await Activate(Admin, module);
        var title = await TitleKey(Admin, module);
        var record = await JsonOf(await Admin.PostAsJsonAsync($"{Base}/modules/{module}/records",
            new { values = new Dictionary<string, object> { [title] = "案件甲" } }));
        var recordId = record.GetProperty("id").GetInt64();
        var onRecord = await NewTask(recordPath: $"{Base}/records/{recordId}/tasks");
        await PostOk(onRecord, Form("記錄任務的圖", File("a.png")));
        var personal = await NewTask();
        await PostOk(personal, Form("個人任務的圖", File("b.png")));
        DiskFiles().Should().HaveCount(2);
        var rowVersion = (await JsonOf(await Admin.GetAsync($"{Base}/records/{recordId}"))).GetProperty("rowVersion").GetString();
        (await Admin.DeleteAsync($"{Base}/records/{recordId}?rowVersion={rowVersion}")).EnsureSuccessStatusCode();

        (await Admin.PostAsync($"{Base}/modules/{module}/deactivate", null)).EnsureSuccessStatusCode();
        var removed = await Admin.DeleteAsync($"{Base}/modules/{module}?purged=1");
        removed.StatusCode.Should().Be(HttpStatusCode.OK, await removed.Content.ReadAsStringAsync());

        DiskFiles().Should().ContainSingle("只刪被清除模組上任務的檔案");
        (await Admin.GetAsync($"{Base}/tasks/{onRecord}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Admin.GetAsync($"{Base}/tasks/{personal}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeleteTask_RemovesRows_FileLeftForSweep()
    {
        var id = await NewTask();
        var comment = await PostOk(id, Form("x", File("a.png")));

        (await Admin.DeleteAsync($"{Base}/tasks/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await Download(Admin, id, comment.GetProperty("id").GetInt64(), comment.GetProperty("attachments")[0].GetProperty("id").GetInt64());
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            db.Set<TaskComment>().Count(c => c.TaskId == id).Should().Be(0);
            db.Set<TaskCommentAttachment>().Count().Should().Be(0, "附件資料列隨任務 cascade 刪除");
        }
        DiskFiles().Should().ContainSingle("刪除單筆任務不同步刪檔");
        var swept = await JsonOf(await Admin.PostAsync($"{Base}/attachments/sweep?graceMinutes=0", null));
        swept.GetProperty("deleted").GetInt32().Should().Be(1);
        DiskFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Comment_BodyOverLimit_413()
    {
        var id = await NewTask();
        var limit = 1024 * 5 + 64 * 1024;

        var res = await Post(id, Form("太大", File("big.png", limit + 1)));

        res.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        DiskFiles().Should().BeEmpty();
    }

    /// <summary>看不到任務、任務不存在、任務凍結時，在收進主體之前就拒絕（主體故意寫壞：讀了主體會是 400）。</summary>
    [Fact]
    public async Task Upload_AccessCheckedBeforeBody()
    {
        var id = await NewTask(_app.AsUser("sales01"));
        static HttpContent Broken()
        {
            var c = new StringContent("--這不是 multipart--");
            c.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=zzz");
            return c;
        }

        var hidden = await _app.AsUser("sales02").PostAsync($"{Base}/tasks/{id}/comments", Broken());
        var missing = await Admin.PostAsync($"{Base}/tasks/{id + 1000}/comments", Broken());

        hidden.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonOf(hidden)).GetProperty("message").GetString().Should().Be("找不到任務");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonOf(missing)).GetProperty("message").GetString().Should().Be("找不到任務");
    }

    private async Task<(string Module, long TaskId)> NewRecordTask()
    {
        var module = await CreateModule(Admin);
        await Activate(Admin, module);
        var title = await TitleKey(Admin, module);
        var record = await JsonOf(await Admin.PostAsJsonAsync($"{Base}/modules/{module}/records",
            new { values = new Dictionary<string, object> { [title] = "案件甲" } }));
        return (module, await NewTask(recordPath: $"{Base}/records/{record.GetProperty("id").GetInt64()}/tasks"));
    }

    private void CloseModule(string module)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        var m = db.Set<Module>().Single(x => x.Key == module);
        m.Status = ModuleStatus.Closed;
        db.SaveChanges();
    }

    [Fact]
    public async Task Upload_FrozenTask_409BeforeBody()
    {
        var (module, id) = await NewRecordTask();
        CloseModule(module);
        var broken = new StringContent("--x--");
        broken.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=zzz");

        var res = await Admin.PostAsync($"{Base}/tasks/{id}/comments", broken);

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ClosedModule_StillDownloads()
    {
        var (module, id) = await NewRecordTask();
        var comment = await PostOk(id, Form("x", File("a.png", 3)));
        CloseModule(module);

        var res = await Download(Admin, id, comment.GetProperty("id").GetInt64(), comment.GetProperty("attachments")[0].GetProperty("id").GetInt64());

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsByteArrayAsync()).Should().HaveCount(3);
    }

    /// <summary>拿別的任務／別的留言的附件 id 走這個任務的路徑：一律 404「找不到附件」，不洩漏附件在別處存在。</summary>
    [Fact]
    public async Task Download_CrossTaskOrCommentIds_404()
    {
        var sales = _app.AsUser("sales01");
        var a = await NewTask();                       // admin 的任務 A，sales01 看不到
        var b = await NewTask(sales);                  // sales01 的任務 B
        var ca = await PostOk(a, Form("A 的檔", File("a.png")));
        var cb = await PostOk(b, Form("B 的檔", File("b.png")), sales);
        var cb2 = await PostOk(b, Form("B 的第二則", File("c.png")), sales);
        long Cid(JsonElement c) => c.GetProperty("id").GetInt64();
        long Aid(JsonElement c) => c.GetProperty("attachments")[0].GetProperty("id").GetInt64();

        var crossTask = await Download(sales, b, Cid(ca), Aid(ca));
        var crossComment = await Download(sales, b, Cid(cb), Aid(cb2));

        foreach (var res in new[] { crossTask, crossComment })
        {
            res.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await JsonOf(res)).GetProperty("message").GetString().Should().Be("找不到附件");
        }
        (await Download(sales, b, Cid(cb2), Aid(cb2))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>兩則留言同時 @ 同一個還不是參與者的人：都不得回 500，參與者只有一列（檔案型資料庫，真正併發）。</summary>
    [Fact]
    public async Task ConcurrentMentions_SameNewUser_No500()
    {
        using var app = new TestApp(fileDatabase: true);
        app.Settings["Logging:LogLevel:Default"] = "Warning";
        var admin = app.AsUser();
        var id = (await JsonOf(await admin.PostAsJsonAsync($"{Base}/tasks", new { title = "併發 @" }))).GetProperty("id").GetInt64();
        var clients = Enumerable.Range(0, 6).Select(_ => app.AsUser()).ToArray();

        var results = await Task.WhenAll(clients.Select((c, i) => c.PostAsJsonAsync($"{Base}/tasks/{id}/comments", new { body = $"第 {i} 則 @{{floor01}}" })));

        results.Select(r => r.StatusCode).Should().OnlyContain(s => s == HttpStatusCode.Created || s == HttpStatusCode.ServiceUnavailable);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        db.Set<TaskParticipant>().Count(p => p.TaskId == id && p.UserId == "floor01").Should().Be(1);
    }
}
