using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests;

/// <summary>Idempotency-Key 標頭經 HTTP（設計 §3.1、§3.2）：重送回放、內容不同 422、格式錯誤 400、沒帶不影響。</summary>
[Trait("Category", "Postgres")]
public class IdempotencyApiTests : IClassFixture<TestApp>
{
    private readonly TestApp _app;
    private readonly HttpClient _admin;

    public IdempotencyApiTests(TestApp app)
    {
        _app = app;
        _admin = app.AsUser("admin");
    }

    private async Task<(string Module, string Title)> NewModuleAsync()
    {
        var m = await CreateModule(_admin);
        await Activate(_admin, m);
        _app.GrantBusinessGroups(m);
        return (m, await TitleKey(_admin, m));
    }

    private static HttpRequestMessage Post(string module, string title, string value, string? key)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/modules/{module}/records")
        {
            Content = JsonContent.Create(new { values = new Dictionary<string, object> { [title] = value } })
        };
        if (key is not null) req.Headers.Add("Idempotency-Key", key);
        return req;
    }

    private async Task<int> CountAsync(string module) =>
        (await JsonOf(await _admin.GetAsync($"{Base}/modules/{module}/records/paged"))).GetProperty("totalCount").GetInt32();

    [Fact]
    public async Task Resending_the_same_request_creates_one_record_and_marks_the_replay()
    {
        var (m, t) = await NewModuleAsync();
        var key = "k-" + Guid.NewGuid().ToString("N");
        var first = await _admin.SendAsync(Post(m, t, "x", key));
        var second = await _admin.SendAsync(Post(m, t, "x", key));

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        first.Headers.Contains("Idempotent-Replay").Should().BeFalse();
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        second.Headers.GetValues("Idempotent-Replay").Should().ContainSingle("true");
        (await JsonOf(second)).GetProperty("id").GetInt64().Should().Be((await JsonOf(first)).GetProperty("id").GetInt64());
        (await CountAsync(m)).Should().Be(1);
    }

    [Fact]
    public async Task Same_key_with_different_content_is_422()
    {
        var (m, t) = await NewModuleAsync();
        var key = "k-" + Guid.NewGuid().ToString("N");
        (await _admin.SendAsync(Post(m, t, "x", key))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await _admin.SendAsync(Post(m, t, "y", key))).StatusCode.Should().Be((HttpStatusCode)422);
        (await CountAsync(m)).Should().Be(1);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("has space in it")]
    [InlineData("中文金鑰中文金鑰中文金鑰")]
    public async Task A_malformed_key_is_rejected_before_anything_runs(string key)
    {
        var (m, t) = await NewModuleAsync();
        var res = await _admin.SendAsync(Post(m, t, "x", key));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CountAsync(m)).Should().Be(0);
    }

    [Fact]
    public async Task Without_a_key_every_request_runs()
    {
        var (m, t) = await NewModuleAsync();
        (await _admin.SendAsync(Post(m, t, "x", null))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await _admin.SendAsync(Post(m, t, "x", null))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await CountAsync(m)).Should().Be(2);
    }

    /// <summary>
    /// 帶 Idempotency-Key 的附件留言（multipart）照常成功、兩次都寫（mcp2a 判斷 5）：任務留言納入請求層冪等後，
    /// 附件留言靠 <c>IdempotencyApplies</c> 放行，不會因為序列化 Stream 變成 500。
    /// </summary>
    [Fact]
    public async Task Multipart_comment_with_key_succeeds_twice()
    {
        var created = await _admin.PostAsJsonAsync($"{Base}/tasks", new { title = "附件冪等" });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var taskId = (await JsonOf(created)).GetProperty("id").GetInt64();
        var key = "k-" + Guid.NewGuid().ToString("N");

        HttpRequestMessage Comment()
        {
            var form = new MultipartFormDataContent { { new StringContent("附件"), "body" } };
            var file = new ByteArrayContent([1, 2, 3]);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "files", "a.png");
            var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/tasks/{taskId}/comments") { Content = form };
            req.Headers.Add("Idempotency-Key", key);
            return req;
        }

        foreach (var _ in new[] { 1, 2 })
        {
            var res = await _admin.SendAsync(Comment());
            res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
            res.Headers.Contains("Idempotent-Replay").Should().BeFalse();
        }
        var list = await JsonOf(await _admin.GetAsync($"{Base}/tasks/{taskId}/comments"));
        list.EnumerateArray().Count(c => c.GetProperty("kind").GetString() == "comment").Should().Be(2);
    }
}
