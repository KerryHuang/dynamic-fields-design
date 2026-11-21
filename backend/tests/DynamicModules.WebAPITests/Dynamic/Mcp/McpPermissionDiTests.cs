using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.WebAPI.Mcp;
using DynamicModules.WebAPITests.Dynamic.PermissionAxes;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using static DynamicModules.WebAPITests.Dynamic.PermissionAxes.PaxWorld;

namespace DynamicModules.WebAPITests.Dynamic.Mcp;

/// <summary>
/// Program.cs 的正式組裝（mcp1a 計畫判斷 10 第 2 點、第三輪必修 1，Task 5）：IPermissionRepository 包上通道遮罩、
/// 通道讀 HttpContext 的 MCP claim。經真的 /mcp 的端到端驗證在 Task 9。
/// </summary>
[Trait("Category", "Postgres")]
public sealed class McpPermissionDiTests : IAsyncLifetime
{
    private readonly TestApp _app = new();
    private PaxWorld _w = null!;
    private long _record;

    public async Task InitializeAsync()
    {
        _w = await CreateAsync(_app);
        var a = _w.Db(db => db.Set<Module>().AsNoTracking().Single(m => m.Key == A));
        var res = await _w.As(Admin).PostAsJsonAsync($"{Base}/modules/{A}/records", new
        {
            values = new Dictionary<string, object> { [F(a.TitleFieldId!.Value)] = "A1", [F(_w.ASalary)] = 50000 },
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        _record = (await JsonOf(res)).GetProperty("id").GetInt64();
        _w.Write(db => db.Set<Field>().Single(f => f.Id == _w.ASalary).ExcludeFromAi = true);
    }

    public Task DisposeAsync()
    {
        _app.Dispose();
        return Task.CompletedTask;
    }

    private static ClaimsPrincipal Mcp(string user) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, user), new Claim(McpClaims.Channel, McpClaims.McpValue), new Claim(McpClaims.Token, "7")], "test"));

    private async Task<T> WithContext<T>(ClaimsPrincipal? principal, Func<IServiceProvider, Task<T>> body)
    {
        var accessor = _app.Services.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;
        using var scope = _app.Services.CreateScope();
        accessor.HttpContext = principal is null ? null : new DefaultHttpContext { User = principal, RequestServices = scope.ServiceProvider };
        try { return await body(scope.ServiceProvider); }
        finally { accessor.HttpContext = previous; }
    }

    private static async Task<bool> AiMasked(IServiceProvider sp, string user)
    {
        var module = (await sp.GetRequiredService<IModuleRepository>().GetByKeyAsync(A, includeChildren: true, CancellationToken.None))!;
        return (await sp.GetRequiredService<IPermissionRepository>().LoadInputsAsync(user, module, CancellationToken.None))!.AiMasked;
    }

    [Fact]
    public void ProgramDi_IsChannelDecorated()
    {
        using var scope = _app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IPermissionRepository>().Should().BeOfType<ChannelPermissionRepository>();
        scope.ServiceProvider.GetRequiredService<IRequestChannel>().Should().BeOfType<HttpRequestChannel>();
    }

    [Fact]
    public async Task ProgramDi_McpClaims_Masked()
    {
        var (masked, values, channel) = await WithContext(Mcp(Admin), async sp =>
        {
            var masked = await AiMasked(sp, Admin);
            var res = await sp.GetRequiredService<ISender>().Send(new GetRecordRequest(_record));
            var ch = sp.GetRequiredService<IRequestChannel>();
            return (masked, ((DynamicRecordResponse)res.Data!).Values, (ch.Channel, ch.TokenId));
        });

        masked.Should().BeTrue();
        channel.Should().Be((RequestChannel.Mcp, 7L));
        values.Keys.Should().NotContain([F(_w.ASalary), F(_w.AF1), F(_w.AF2)]);
    }

    [Fact]
    public async Task ProgramDi_Ui_Unchanged()
    {
        var rest = await JsonOf(await _w.As(Admin).GetAsync($"{Base}/records/{_record}"));
        rest.GetProperty("values").GetProperty(F(_w.ASalary)).GetDecimal().Should().Be(50000);
        rest.GetProperty("values").TryGetProperty(F(_w.AF1), out _).Should().BeTrue();

        (await WithContext(null, sp => AiMasked(sp, Admin))).Should().BeFalse("沒有 HttpContext（背景工作）是畫面通道");
        var ui = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Admin)], "test"));
        (await WithContext(ui, sp => AiMasked(sp, Admin))).Should().BeFalse();
    }
}
