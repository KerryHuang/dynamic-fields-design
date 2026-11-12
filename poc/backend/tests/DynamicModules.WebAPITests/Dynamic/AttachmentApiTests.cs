using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>附件欄位：上傳、下載、安全邊界、生命週期。每個測試自己一個 TestApp，才能獨立設定與檢查磁碟。</summary>
public class AttachmentApiTests : IDisposable
{
    private readonly TestApp _app = new();
    private HttpClient? _admin;

    private HttpClient Admin => _admin ??= _app.AsUser();

    public void Dispose() => _app.Dispose();

    private sealed record World(string Module, long FieldId, string FieldKey, string TitleKey, long RecordId, string RowVersion);

    /// <summary>啟用的模組：標題欄位加一個附件欄位，並已有一筆記錄。</summary>
    private async Task<World> MakeWorld(bool grant = true)
    {
        var module = await CreateModule(Admin);
        var field = await AddField(Admin, module, "attachment");
        await Activate(Admin, module);
        if (grant) _app.GrantBusinessGroups(module);
        var title = await TitleKey(Admin, module);
        var created = await JsonOf(await Admin.PostAsJsonAsync($"{Base}/modules/{module}/records",
            new { values = new Dictionary<string, object> { [title] = "t" } }));
        return new World(module, field.Id, field.Key, title,
            created.GetProperty("id").GetInt64(), created.GetProperty("rowVersion").GetString()!);
    }

    private static MultipartFormDataContent Form(string fileName, byte[] bytes)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent { { file, "file", fileName } };
    }

    private Task<HttpResponseMessage> Upload(HttpClient c, World w, string fileName, byte[]? bytes = null) =>
        c.PostAsync($"{Base}/records/{w.RecordId}/attachments/{w.FieldId}", Form(fileName, bytes ?? "hello"u8.ToArray()));

    private async Task<string> UploadOk(World w, string fileName = "a.txt", byte[]? bytes = null)
    {
        var res = await Upload(Admin, w, fileName, bytes);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("id").GetString()!;
    }

    private Task<HttpResponseMessage> Download(HttpClient c, World w, string fileId) =>
        c.GetAsync($"{Base}/records/{w.RecordId}/attachments/{w.FieldId}/{fileId}");

    private async Task<JsonElement> RecordValues(World w) =>
        (await JsonOf(await Admin.GetAsync($"{Base}/records/{w.RecordId}"))).GetProperty("values");

    private static Task<HttpResponseMessage> PutSystemPolicy(HttpClient c, string code, object? value) =>
        c.PutAsJsonAsync($"{Base}/system/policies/{code}", new { value });

    private static Task<HttpResponseMessage> PutModulePolicy(HttpClient c, string module, string code, object? value) =>
        c.PutAsJsonAsync($"{Base}/modules/{module}/policies/{code}", new { value });

    private async Task<JsonElement> FactoryOf(string code) =>
        (await JsonOf(await Admin.GetAsync($"{Base}/system/policies"))).EnumerateArray()
        .Single(e => e.GetProperty("code").GetString() == code).GetProperty("factory");

    private string[] DiskFiles() =>
        Directory.Exists(_app.AttachmentRoot) ? Directory.GetFiles(_app.AttachmentRoot, "*", SearchOption.AllDirectories) : [];

    // ---- 基本往返與回應標頭 ----

    [Fact]
    public async Task UploadThenDownload_ReturnsSameBytes_WithAttachmentAndNosniffHeaders()
    {
        var w = await MakeWorld();
        var id = await UploadOk(w, "報價.pdf", "pdf-bytes"u8.ToArray());

        var values = await RecordValues(w);
        var list = values.GetProperty(w.FieldKey);
        list.GetArrayLength().Should().Be(1);
        list[0].GetProperty("name").GetString().Should().Be("報價.pdf");
        list[0].GetProperty("size").GetInt64().Should().Be(9);

        var res = await Download(Admin, w, id);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsByteArrayAsync()).Should().Equal("pdf-bytes"u8.ToArray());
        res.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        res.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
        res.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which.Should().Be("nosniff");
        res.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task Upload_MultipleFiles_AllKeptInOrder()
    {
        var w = await MakeWorld();
        await UploadOk(w, "a.txt");
        await UploadOk(w, "b.txt");
        var list = (await RecordValues(w)).GetProperty(w.FieldKey);
        list.EnumerateArray().Select(f => f.GetProperty("name").GetString()).Should().Equal("a.txt", "b.txt");
    }

    // ---- 檔名不被信任 ----

    [Theory]
    [InlineData("../../etc/passwd.txt", "passwd.txt")]
    [InlineData("..\\..\\windows\\evil.txt", "evil.txt")]
    [InlineData("/abs/path/file.txt", "file.txt")]
    public async Task Upload_PathInFileName_IsStrippedAndNeverTouchesDisk(string sent, string expectedName)
    {
        var w = await MakeWorld();
        await UploadOk(w, sent);
        (await RecordValues(w)).GetProperty(w.FieldKey)[0].GetProperty("name").GetString().Should().Be(expectedName);

        var files = DiskFiles();
        files.Should().ContainSingle();
        Path.GetFileName(files[0]).Should().MatchRegex("^[0-9a-f]{32}$");
        Path.GetFullPath(files[0]).Should().StartWith(Path.GetFullPath(_app.AttachmentRoot));
    }

    // ---- 副檔名白名單 ----

    [Theory]
    [InlineData("run.exe")]
    [InlineData("page.html")]
    [InlineData("image.svg")]
    [InlineData("script.sh")]
    [InlineData("macro.js")]
    [InlineData("a.pdf.exe")]
    [InlineData("noextension")]
    [InlineData("trailing.")]
    [InlineData("archive.zip")]
    public async Task Upload_ExtensionNotInWhitelist_400AndNothingWritten(string name)
    {
        var w = await MakeWorld();
        var res = await Upload(Admin, w, name);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        DiskFiles().Should().BeEmpty();
        (await RecordValues(w)).TryGetProperty(w.FieldKey, out var v).Should().Be(false, v.ToString());
    }

    [Fact]
    public async Task Upload_UppercaseExtensionOnWhitelist_IsAccepted()
    {
        var w = await MakeWorld();
        (await Upload(Admin, w, "SCAN.PNG")).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ---- 大小與數量 ----

    [Fact]
    public async Task Upload_OverMaxBytes_400AndPartialFileRemoved()
    {
        _app.Settings["Attachments:MaxBytes"] = "10";
        var w = await MakeWorld();
        (await Upload(Admin, w, "ok.txt", new byte[10])).StatusCode.Should().Be(HttpStatusCode.Created);
        var res = await Upload(Admin, w, "big.txt", new byte[11]);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        DiskFiles().Should().ContainSingle();
    }

    [Fact]
    public async Task Upload_OverMaxFilesPerField_400()
    {
        _app.Settings["Attachments:MaxFilesPerField"] = "2";
        var w = await MakeWorld();
        await UploadOk(w, "1.txt");
        await UploadOk(w, "2.txt");
        (await Upload(Admin, w, "3.txt")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Upload_EmptyFile_400()
    {
        var w = await MakeWorld();
        (await Upload(Admin, w, "empty.txt", [])).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- 一般儲存只能保留或移除，不能偽造 ----

    [Fact]
    public async Task UpdateRecord_ForgedAttachmentId_400()
    {
        var w = await MakeWorld();
        var res = await Admin.PutAsJsonAsync($"{Base}/records/{w.RecordId}", new
        {
            rowVersion = w.RowVersion,
            values = new Dictionary<string, object>
            {
                [w.TitleKey] = "t",
                [w.FieldKey] = new[] { new { id = new string('a', 32), name = "evil.txt", size = 1 } }
            }
        });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateRecord_KeepingOrRemovingExistingFiles_Works()
    {
        var w = await MakeWorld();
        var keep = await UploadOk(w, "keep.txt");
        var drop = await UploadOk(w, "drop.txt");
        var current = await JsonOf(await Admin.GetAsync($"{Base}/records/{w.RecordId}"));
        var rowVersion = current.GetProperty("rowVersion").GetString();
        var kept = current.GetProperty("values").GetProperty(w.FieldKey).EnumerateArray()
            .Where(f => f.GetProperty("id").GetString() == keep).Select(f => (object)JsonSerializer.Deserialize<Dictionary<string, object>>(f.GetRawText())!).ToArray();

        var res = await Admin.PutAsJsonAsync($"{Base}/records/{w.RecordId}", new
        {
            rowVersion,
            values = new Dictionary<string, object> { [w.TitleKey] = "t", [w.FieldKey] = kept }
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        (await Download(Admin, w, keep)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Download(Admin, w, drop)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- 下載的權限 ----

    [Fact]
    public async Task Download_UserWithoutModuleAccess_IsDenied()
    {
        var module = await CreateModule(Admin);
        var field = await AddField(Admin, module, "attachment");
        await Activate(Admin, module); // 沒有 GrantBusinessGroups：業務沒有任何權限
        var title = await TitleKey(Admin, module);
        var created = await JsonOf(await Admin.PostAsJsonAsync($"{Base}/modules/{module}/records",
            new { values = new Dictionary<string, object> { [title] = "t" } }));
        var w = new World(module, field.Id, field.Key, title, created.GetProperty("id").GetInt64(), created.GetProperty("rowVersion").GetString()!);
        var id = await UploadOk(w);

        var res = await Download(_app.AsUser("sales01"), w, id);
        res.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
        (await Download(_app.AsUser(null), w, id)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Download_FileIdFromAnotherRecordOrUnknown_404()
    {
        var w = await MakeWorld();
        var other = await JsonOf(await Admin.PostAsJsonAsync($"{Base}/modules/{w.Module}/records",
            new { values = new Dictionary<string, object> { [w.TitleKey] = "other" } }));
        var otherW = w with { RecordId = other.GetProperty("id").GetInt64() };
        var id = await UploadOk(w);

        (await Download(Admin, otherW, id)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Download(Admin, w, new string('0', 32))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Download(Admin, w, "..%2f..%2fetc%2fpasswd")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Download(Admin, w, "not-a-hex-id")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Upload_WithoutEditPermission_IsDenied()
    {
        var w = await MakeWorld();
        // sales01 被授權後再拿掉編輯權的情境由權限測試涵蓋；這裡驗未登入與無權限帳號
        (await Upload(_app.AsUser(null), w, "a.txt")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- 記錄生命週期 ----

    [Fact]
    public async Task SoftDeletedRecord_CannotDownload_ButFileIsKeptBySweep()
    {
        var w = await MakeWorld();
        var id = await UploadOk(w);
        var rowVersion = (await JsonOf(await Admin.GetAsync($"{Base}/records/{w.RecordId}"))).GetProperty("rowVersion").GetString();
        (await Admin.DeleteAsync($"{Base}/records/{w.RecordId}?rowVersion={rowVersion}")).EnsureSuccessStatusCode();

        (await Download(Admin, w, id)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var sweep = await Admin.PostAsync($"{Base}/attachments/sweep?graceMinutes=0", null);
        sweep.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(sweep)).GetProperty("deleted").GetInt32().Should().Be(0);
        DiskFiles().Should().ContainSingle("軟刪除的記錄可能被還原，檔案要留著");
    }

    [Fact]
    public async Task PurgedModule_LeavesOrphan_SweepRemovesItAfterGrace()
    {
        var w = await MakeWorld();
        await UploadOk(w);
        var rowVersion = (await JsonOf(await Admin.GetAsync($"{Base}/records/{w.RecordId}"))).GetProperty("rowVersion").GetString();
        (await Admin.DeleteAsync($"{Base}/records/{w.RecordId}?rowVersion={rowVersion}")).EnsureSuccessStatusCode();
        (await Admin.PostAsync($"{Base}/modules/{w.Module}/deactivate", null)).EnsureSuccessStatusCode();
        (await Admin.DeleteAsync($"{Base}/modules/{w.Module}?purged=1")).StatusCode.Should().Be(HttpStatusCode.OK);
        DiskFiles().Should().ContainSingle("永久清除不同步刪檔，交給清掃");

        // 寬限時間內不動（剛寫入的檔案可能還在上傳流程中）
        var withinGrace = await JsonOf(await Admin.PostAsync($"{Base}/attachments/sweep?graceMinutes=60", null));
        withinGrace.GetProperty("deleted").GetInt32().Should().Be(0);
        DiskFiles().Should().ContainSingle();

        var swept = await JsonOf(await Admin.PostAsync($"{Base}/attachments/sweep?graceMinutes=0", null));
        swept.GetProperty("deleted").GetInt32().Should().Be(1);
        DiskFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_RequiresSystemAdmin()
    {
        var res = await _app.AsUser("sales01").PostAsync($"{Base}/attachments/sweep", null);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Sweep_KeepsReferencedFiles()
    {
        var w = await MakeWorld();
        var id = await UploadOk(w);
        (await JsonOf(await Admin.PostAsync($"{Base}/attachments/sweep?graceMinutes=0", null))).GetProperty("deleted").GetInt32().Should().Be(0);
        (await Download(Admin, w, id)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- Closed 模組：能下載、不能上傳 ----

    [Fact]
    public async Task ClosedModule_AllowsDownload_BlocksUpload()
    {
        var w = await MakeWorld();
        var id = await UploadOk(w);
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            db.Set<Module>().Single(m => m.Key == w.Module).Status = ModuleStatus.Closed;
            db.SaveChanges();
        }

        (await Download(Admin, w, id)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Upload(Admin, w, "late.txt")).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---- 請求主體在檢查之前不被吞進來（fix-1 #3）----

    /// <summary>送出 length 位元組的假 multipart 主體，記錄實際被寫出去（也就是被伺服器讀走）多少。</summary>
    private sealed class ProbeContent : HttpContent
    {
        private readonly long _length;
        public long Written;

        public ProbeContent(long length)
        {
            _length = length;
            Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=probe");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buffer = new byte[16 * 1024];
            while (Written < _length)
            {
                var n = (int)Math.Min(buffer.Length, _length - Written);
                await stream.WriteAsync(buffer.AsMemory(0, n));
                Written += n;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }

    private const long ProbeSize = 8L * 1024 * 1024;

    private async Task<(HttpResponseMessage Response, ProbeContent Body)> UploadProbe(HttpClient c, World w, long size = ProbeSize)
    {
        var body = new ProbeContent(size);
        _app.ResetBodyCount();
        var res = await c.PostAsync($"{Base}/records/{w.RecordId}/attachments/{w.FieldId}", body);
        return (res, body);
    }

    [Fact]
    public async Task Upload_Disabled_RejectsWithoutReadingTheBody()
    {
        _app.Settings["Attachments:UploadEnabled"] = "false";
        var w = await MakeWorld();
        var (res, _) = await UploadProbe(Admin, w);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _app.BodyBytesRead.Should().BeLessThan(ProbeSize / 4, "上傳關閉時不該把整個請求收進來");
    }

    [Fact]
    public async Task Upload_Disabled_StaysRejected_EvenIfServerScopeIsActiveInTheSameProcess()
    {
        _app.Settings["Attachments:UploadEnabled"] = "false";
        // TestServer 預設不把測試端的 ExecutionContext 帶進請求；不開這個，範圍根本流不進伺服器，測試永遠綠。
        // 這個屬性在建 client（handler）時才被複製，所以要在任何 client 建立之前設
        _app.Server.PreserveExecutionContext = true;
        var w = await MakeWorld();
        // 伺服器內部範圍只能由程式碼開；同行程裡有範圍開著（且真的流進請求）也不影響 HTTP 的閘門
        using var _ = DynamicModules.Application.Common.Uploads.ServerUploadScope.Begin();
        var (res, _) = await UploadProbe(Admin, w);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        // 以下兩個標頭只證明「沒有這兩個名字的入口」，不是通用的「請求無法開範圍」保證
        foreach (var header in new[] { "X-Server-Upload", "X-Upload-Enabled" })
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/records/{w.RecordId}/attachments/{w.FieldId}") { Content = new ProbeContent(10) };
            req.Headers.Add(header, "true");
            (await Admin.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Conflict, "請求標頭不能繞過上傳開關");
        }
    }

    [Fact]
    public async Task Upload_ContentLengthOverTheLimit_413WithoutReadingTheBody()
    {
        _app.Settings["Attachments:MaxBytes"] = "1000";
        var w = await MakeWorld();
        var (res, _) = await UploadProbe(Admin, w);
        res.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        _app.BodyBytesRead.Should().BeLessThan(ProbeSize / 4);
    }

    [Fact]
    public async Task Upload_WithoutEditPermission_RejectedBeforeTheBodyIsRead()
    {
        var w = await MakeWorld(grant: false); // 業務群組沒有這個模組的任何權限
        var (res, _) = await UploadProbe(_app.AsUser("sales01"), w);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _app.BodyBytesRead.Should().BeLessThan(ProbeSize / 4);
    }

    [Fact]
    public async Task Upload_UnknownRecord_404WithoutReadingTheBody()
    {
        var w = await MakeWorld();
        var body = new ProbeContent(ProbeSize);
        _app.ResetBodyCount();
        var res = await Admin.PostAsync($"{Base}/records/987654/attachments/{w.FieldId}", body);
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _app.BodyBytesRead.Should().BeLessThan(ProbeSize / 4);
    }

    [Fact]
    public async Task Upload_JustUnderTheLimit_StillWorks()
    {
        _app.Settings["Attachments:MaxBytes"] = "200000";
        var w = await MakeWorld();
        (await Upload(Admin, w, "big.txt", new byte[200000])).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public void UploadGuard_SetsTheBodySizeFeature_ForChunkedRequestsToo()
    {
        var feature = new FakeBodySizeFeature();
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>(feature); // 沒有 Content-Length

        DynamicModules.WebAPI.Controllers.v3.Dynamic.UploadGuard.ApplyBodyLimit(http, 1000L).Should().BeNull();

        feature.MaxRequestBodySize.Should().BeInRange(1000, 1000 + 256 * 1024);
    }

    private sealed class FakeBodySizeFeature : Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; } = 64L * 1024 * 1024;
    }

    // ---- A20 附件政策：部署設定檔當出廠值、模組覆寫、危險副檔名黑名單 ----

    [Fact]
    public async Task Settings_ConfigValueIsEnvironmentFactory()
    {
        _app.Settings["Attachments:MaxFilesPerField"] = "2";
        _app.Settings["Attachments:AllowedExtensions:0"] = ".PDF";
        _app.Settings["Attachments:AllowedExtensions:1"] = "txt";

        (await FactoryOf("attachment.maxFilesPerField")).GetInt32().Should().Be(2);
        (await FactoryOf("attachment.allowedExtensions")).EnumerateArray().Select(e => e.GetString()).Should().Equal("pdf", "txt");
        (await FactoryOf("attachment.maxFileBytes")).GetInt64().Should().Be(10485760, "設定檔沒寫的鍵用程式出廠值");
        var settings = await JsonOf(await Admin.GetAsync($"{Base}/attachments/settings"));
        settings.GetProperty("maxFilesPerField").GetInt32().Should().Be(2);
        settings.GetProperty("allowedExtensions").EnumerateArray().Select(e => e.GetString()).Should().Equal("pdf", "txt");
    }

    [Fact]
    public async Task Settings_ConfigOutOfRange_Clamped()
    {
        _app.Settings["Attachments:MaxBytes"] = "999999999";

        (await FactoryOf("attachment.maxFileBytes")).GetInt64().Should().Be(52428800);
    }

    [Fact]
    public async Task Settings_ConfigForbiddenExtension_DroppedFromFactory()
    {
        _app.Settings["Attachments:AllowedExtensions:0"] = "pdf";
        _app.Settings["Attachments:AllowedExtensions:1"] = "exe";

        (await FactoryOf("attachment.allowedExtensions")).EnumerateArray().Select(e => e.GetString()).Should().Equal("pdf");
    }

    [Fact]
    public async Task Upload_ModuleOverrideMaxFiles_UsesOverride()
    {
        var w = await MakeWorld();
        (await PutModulePolicy(Admin, w.Module, "attachment.maxFilesPerField", 1)).StatusCode.Should().Be(HttpStatusCode.OK);

        await UploadOk(w, "1.txt");
        (await Upload(Admin, w, "2.txt")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // 全系統值沒動，別的模組不受影響
        (await FactoryOf("attachment.maxFilesPerField")).GetInt32().Should().Be(20);
    }

    [Fact]
    public async Task Upload_FieldAlreadyOverLoweredLimit_Rejected()
    {
        var w = await MakeWorld();
        var ids = new List<string>();
        for (var i = 1; i <= 3; i++) ids.Add(await UploadOk(w, $"{i}.txt"));
        (await PutModulePolicy(Admin, w.Module, "attachment.maxFilesPerField", 2)).EnsureSuccessStatusCode();

        (await Upload(Admin, w, "4.txt")).StatusCode.Should().Be(HttpStatusCode.BadRequest, "已超標的欄位不能再增加");
        (await Download(Admin, w, ids[0])).StatusCode.Should().Be(HttpStatusCode.OK, "既有檔案照常可讀");

        // 刪到只剩 1 個（存記錄）後可以再傳
        var rec = await JsonOf(await Admin.GetAsync($"{Base}/records/{w.RecordId}"));
        var keep = rec.GetProperty("values").GetProperty(w.FieldKey)[0];
        var saved = await Admin.PutAsJsonAsync($"{Base}/records/{w.RecordId}", new
        {
            rowVersion = rec.GetProperty("rowVersion").GetString(),
            values = new Dictionary<string, object> { [w.FieldKey] = new[] { keep } }
        });
        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        await UploadOk(w, "5.txt");
    }

    [Fact]
    public async Task Upload_ModuleOverrideMaxBytes_413WithoutReadingTheBody()
    {
        var w = await MakeWorld();
        (await PutModulePolicy(Admin, w.Module, "attachment.maxFileBytes", 1000)).EnsureSuccessStatusCode();

        var (res, _) = await UploadProbe(Admin, w);

        res.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        _app.BodyBytesRead.Should().BeLessThan(ProbeSize / 4, "主體上限用的是該模組的覆寫值，不是全系統值");
    }

    [Fact]
    public async Task Upload_ExtensionRemovedFromWhitelist_ExistingDownloadStillWorks()
    {
        var w = await MakeWorld();
        var id = await UploadOk(w, "a.txt");

        (await PutSystemPolicy(Admin, "attachment.allowedExtensions", new[] { "pdf" })).EnsureSuccessStatusCode();

        (await Upload(Admin, w, "b.txt")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Download(Admin, w, id)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Put_ForbiddenExtension_400ListsTheRejectedOnes()
    {
        var res = await PutSystemPolicy(Admin, "attachment.allowedExtensions", new[] { "pdf", "EXE", ".svg" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var message = (await JsonOf(res)).GetProperty("errors").GetProperty("value")[0].GetString()!;
        message.Should().Contain("exe").And.Contain("svg").And.NotContain("pdf");
        (await FactoryOf("attachment.allowedExtensions")).GetArrayLength().Should().Be(10, "沒存進去");
    }

    [Fact]
    public async Task Upload_ForbiddenExtensionInStoredList_Rejected()
    {
        // 手改資料庫塞進黑名單項目：整個值視為壞值，上傳仍不收
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            db.Set<SystemSetting>().Add(new SystemSetting { Key = "policy.attachment.allowedExtensions", Value = "txt,exe,svg" });
            db.SaveChanges();
        }
        var w = await MakeWorld();

        (await Upload(Admin, w, "run.exe")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Upload(Admin, w, "pic.svg")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        DiskFiles().Should().BeEmpty();
        (await Upload(Admin, w, "a.txt")).StatusCode.Should().Be(HttpStatusCode.Created, "壞值退回的是出廠白名單，txt 仍可上傳");
    }

    // ---- Demo / 環境開關 ----

    [Fact]
    public async Task UploadDisabledByConfig_Returns409WithReason_AndSettingsExplainIt()
    {
        _app.Settings["Attachments:UploadEnabled"] = "false";
        _app.Settings["Attachments:DisabledReason"] = "展示站磁碟會在重新部署時清空";
        var w = await MakeWorld();

        var settings = await JsonOf(await Admin.GetAsync($"{Base}/attachments/settings"));
        settings.GetProperty("uploadEnabled").GetBoolean().Should().BeFalse();
        settings.GetProperty("disabledReason").GetString().Should().Contain("重新部署");
        settings.ToString().Should().NotContain(_app.AttachmentRoot, "不外洩伺服器路徑");

        var res = await Upload(Admin, w, "a.txt");
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await res.Content.ReadAsStringAsync()).Should().Contain("重新部署");
        DiskFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task DemoLoginEnabled_DisablesUploadByDefault_ExplicitSettingOverrides()
    {
        _app.Settings["Demo:LoginEnabled"] = "true";
        (await JsonOf(await Admin.GetAsync($"{Base}/attachments/settings"))).GetProperty("uploadEnabled").GetBoolean().Should().BeFalse();

        using var overridden = new TestApp();
        overridden.Settings["Demo:LoginEnabled"] = "true";
        overridden.Settings["Attachments:UploadEnabled"] = "true";
        (await JsonOf(await overridden.AsUser().GetAsync($"{Base}/attachments/settings"))).GetProperty("uploadEnabled").GetBoolean().Should().BeTrue();
    }

    // ---- 設計器：明細模組不能有附件欄位 ----

    [Fact]
    public async Task AddAttachmentField_InDetailModule_Rejected()
    {
        var header = await CreateModule(Admin);
        var detail = await CreateModule(Admin);
        var module = await JsonOf(await Admin.GetAsync($"{Base}/modules/{detail}"));
        var blockId = module.GetProperty("blocks")[0].GetProperty("id").GetInt64();
        (await Admin.PostAsJsonAsync($"{Base}/modules/{detail}/fields", new
        {
            blockId, type = "reference", label = new Dictionary<string, string> { ["zh-TW"] = "表頭" },
            config = new { refTarget = new { kind = "module", key = header }, relation = "detail" }
        })).EnsureSuccessStatusCode();

        var res = await Admin.PostAsJsonAsync($"{Base}/modules/{detail}/fields", new
        {
            blockId, type = "attachment", label = new Dictionary<string, string> { ["zh-TW"] = "附件" }, config = new { }
        });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- 匯入、匯出、報表：明確行為、不 500 ----

    [Fact]
    public async Task Export_ShowsFileNames_NeverFails()
    {
        var w = await MakeWorld();
        await UploadOk(w, "a.txt");
        await UploadOk(w, "b.pdf");

        foreach (var format in new[] { "csv", "xlsx" })
        {
            var res = await Admin.GetAsync($"{Base}/modules/{w.Module}/export?format={format}");
            res.StatusCode.Should().Be(HttpStatusCode.OK, format);
            if (format == "csv") (await res.Content.ReadAsStringAsync()).Should().Contain("a.txt; b.pdf");
        }
    }

    [Fact]
    public async Task Import_AttachmentColumn_IsRejectedExplicitly_Not500()
    {
        var w = await MakeWorld();
        var field = await JsonOf(await Admin.GetAsync($"{Base}/modules/{w.Module}"));
        var label = field.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .First(f => f.GetProperty("id").GetInt64() == w.FieldId).GetProperty("label").GetProperty("zh-TW").GetString();
        var csv = Encoding.UTF8.GetBytes($"{w.TitleKey},{label}\r\nx,evil.txt\r\n");
        var file = new ByteArrayContent(csv);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        var res = await Admin.PostAsync($"{Base}/modules/{w.Module}/import?commit=true",
            new MultipartFormDataContent { { file, "file", "import.csv" } });
        ((int)res.StatusCode).Should().BeLessThan(500);
        (await res.Content.ReadAsStringAsync()).Should().Contain("附件欄位不支援匯入");
    }

    [Fact]
    public async Task ReportCatalog_DoesNotOfferAttachmentField()
    {
        var w = await MakeWorld();
        var res = await Admin.GetAsync($"{Base}/reports/catalog/{w.Module}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync()).Should().NotContain($"\"fieldId\":{w.FieldId},");
    }
}
