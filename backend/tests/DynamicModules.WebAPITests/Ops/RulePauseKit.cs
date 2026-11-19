using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Observability;
using DynamicModules.Application.Dynamic.OpsService.RulePause;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using DynamicModules.TestSupport;

namespace DynamicModules.WebAPITests.Ops;

/// <summary>測試用的故障注入：依規則代碼指定要丟的例外，可隨時拿掉（E15 的「可逐次控制」）。</summary>
internal sealed class ScriptedFaults : ITaskRuleFaultInjector
{
    private readonly ConcurrentDictionary<string, Func<Exception>> _faults = new();

    public void Fail(string ruleKey, Func<Exception>? make = null) => _faults[ruleKey] = make ?? (() => new InjectedFaultException(ruleKey));

    public void Heal(string ruleKey) => _faults.TryRemove(ruleKey, out _);

    public void Inject(TaskRule rule)
    {
        if (_faults.TryGetValue(rule.Key, out var make)) throw make();
    }
}

/// <summary>擋在補記失敗之前（最外層、交易之外）：E05、E15、E16 讓請求停在「業務已回滾、還沒補記」。</summary>
internal sealed class WriteBackGate
{
    private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _arrived = new(0);
    private volatile bool _closed;

    public void Close()
    {
        _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _closed = true;
    }

    public void Open()
    {
        _closed = false;
        _release.TrySetResult();
    }

    /// <summary>等第 n 個請求停在閘門前（有上限，避免卡死整個測試）。</summary>
    public async Task WaitArrivalAsync()
    {
        if (!await _arrived.WaitAsync(TimeSpan.FromMinutes(2))) throw new TimeoutException("沒有請求停在補記之前");
    }

    public async Task PassAsync()
    {
        if (!_closed) return;
        var release = _release.Task;
        _arrived.Release();
        await release;
    }
}

internal sealed class WriteBackGateBehavior(WriteBackGate gate) : IPipelineBehavior<RecordRuleFailureRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(RecordRuleFailureRequest request, RequestHandlerDelegate<ServiceResult> next, CancellationToken ct)
    {
        await gate.PassAsync();
        return await next();
    }
}

/// <summary>o2d 測試共用：一個啟用任務的模組（標題、負責人、旗標、備註）、由設計者 planner01 建的任務規則、健康列與事件的讀取。</summary>
internal sealed class RulePauseRig : IDisposable
{
    public const string Base = "/api/v3/dynamic";
    public const string Designer = "planner01";

    public TestApp App { get; }
    public ScriptedFaults Faults { get; } = new();
    public WriteBackGate Gate { get; } = new();
    public HttpClient Admin { get; }
    public HttpClient Planner { get; }
    public string Module { get; private set; } = "";
    public string Title { get; private set; } = "";
    public (long Id, string Key) Owner { get; private set; }
    public (long Id, string Key) Flag { get; private set; }
    public (long Id, string Key) Note { get; private set; }

    public RulePauseRig(bool fileDatabase = false, bool withoutWriteSerialization = false, Action<IServiceCollection>? extra = null,
        Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor? interceptor = null, string? sharedConnectionString = null)
    {
        App = new TestApp(seedDemoPeople: sharedConnectionString is null, fileDatabase: fileDatabase, sharedConnectionString: sharedConnectionString) { WithoutWriteSerialization = withoutWriteSerialization };
        if (interceptor is not null) App.Interceptors.Add(interceptor);
        App.Settings["Logging:LogLevel:Default"] = "Warning";
        App.ExtraServices = s =>
        {
            s.RemoveAll<ITaskRuleFaultInjector>();
            s.AddSingleton<ITaskRuleFaultInjector>(Faults);
            s.AddSingleton(Gate);
            // 放在最前面＝最外層（在交易之外），才是「業務已回滾、還沒補記」
            s.Insert(0, ServiceDescriptor.Transient<IPipelineBehavior<RecordRuleFailureRequest, ServiceResult>, WriteBackGateBehavior>());
            extra?.Invoke(s);
        };
        Admin = App.AsUser();
        Planner = App.AsUser(Designer);
    }

    public void Dispose() => App.Dispose();

    /// <summary>第二個實例（同一個資料庫、另一份記憶體與 DI）：沿用第一個的模組與欄位。</summary>
    public RulePauseRig SecondInstance()
    {
        var other = new RulePauseRig(sharedConnectionString: App.ConnectionString)
        {
            Module = Module, Title = Title, Owner = Owner, Flag = Flag, Note = Note,
        };
        return other;
    }

    public async Task BuildAsync()
    {
        GrantDesigner(Designer, edit: true);
        Module = await CreateModule(Admin);
        var owner = await AddField(Admin, Module, "user");
        var flag = await AddField(Admin, Module, "boolean");
        var note = await AddField(Admin, Module, "text");
        await Activate(Admin, Module);
        App.GrantBusinessGroups(Module);
        Title = await TitleKey(Admin, Module);
        (Owner, Flag, Note) = ((owner.Id, owner.Key), (flag.Id, flag.Key), (note.Id, note.Key));
    }

    public void GrantDesigner(string userId, bool edit)
    {
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        var row = db.Set<UserProgramPrivilege>().SingleOrDefault(p => p.UserId == userId && p.ProgramCode == "DYN00000");
        if (row is null)
        {
            row = new UserProgramPrivilege { UserId = userId, ProgramCode = "DYN00000" };
            db.Set<UserProgramPrivilege>().Add(row);
        }
        row.CanRead = true;
        row.CanAdd = edit;
        row.CanEdit = edit;
        row.CanDelete = edit;
        db.SaveForTests();
    }

    /// <summary>由設計者 planner01 建規則（最後修改者＝planner01）；回傳規則 id。</summary>
    public async Task<long> CreateRuleAsync(string key, string on = "onCreate", object? when = null, string repeat = "once")
    {
        var res = await Planner.PostAsJsonAsync($"{Base}/modules/{Module}/task-rules", new
        {
            key, name = new Dictionary<string, string> { ["zh-TW"] = key }, active = true, repeat, trigger = new { on }, when,
            tasks = new[] { new { code = "a", title = new Dictionary<string, string> { ["zh-TW"] = "跟進" }, assigneeFieldId = Owner.Id, dueOffsetDays = 0 } },
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        await using var scope = App.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<TaskRule>().Where(r => r.Key == key).Select(r => r.Id).SingleAsync();
    }

    public object FlagIsTrue => new { all = new[] { new { field = Flag.Id, op = "eq", value = true } } };

    public Task<HttpResponseMessage> PostRecordAsync(bool flag = false, HttpClient? client = null) =>
        (client ?? Admin).PostAsJsonAsync($"{Base}/modules/{Module}/records", new
        {
            values = new Dictionary<string, object> { [Title] = "案", [Owner.Key] = "sales02", [Flag.Key] = flag },
        });

    public async Task<long> NewRecordAsync(bool flag = false)
    {
        var res = await PostRecordAsync(flag);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    public async Task<HttpResponseMessage> PutRecordAsync(long id, Dictionary<string, object> values)
    {
        var current = await JsonOf(await Admin.GetAsync($"{Base}/records/{id}"));
        return await Admin.PutAsJsonAsync($"{Base}/records/{id}", new { values, rowVersion = current.GetProperty("rowVersion").GetString() });
    }

    /// <summary>連存 n 筆新記錄，全部應回 500（規則注入失敗）；每次之間推進一秒，評估時間才不同。</summary>
    public async Task FailCreatesAsync(int n, HttpStatusCode expected = HttpStatusCode.InternalServerError)
    {
        for (var i = 0; i < n; i++)
        {
            Tick();
            var res = await PostRecordAsync();
            res.StatusCode.Should().Be(expected, await res.Content.ReadAsStringAsync());
        }
    }

    public void Tick(double seconds = 1) => App.Time.Advance(TimeSpan.FromSeconds(seconds));

    public async Task<JsonElement> RecordTasksAsync(long recordId) =>
        await JsonOf(await Admin.GetAsync($"{Base}/records/{recordId}/tasks"));

    public Task<HttpResponseMessage> ResumeAsync(string ruleKey, HttpClient? client = null) =>
        (client ?? Planner).PostAsync($"{Base}/modules/{Module}/task-rules/{ruleKey}/resume", null);

    public async Task<JsonElement> RuleAsync(string ruleKey) =>
        (await JsonOf(await Planner.GetAsync($"{Base}/modules/{Module}/task-rules"))).EnumerateArray()
        .Single(r => r.GetProperty("key").GetString() == ruleKey);

    private async Task<T> Db<T>(Func<DynamicDbContext, Task<T>> read)
    {
        await using var scope = App.Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<DynamicDbContext>());
    }

    public Task<RuleHealth?> HealthAsync(long ruleId) =>
        Db(db => db.Set<RuleHealth>().AsNoTracking().SingleOrDefaultAsync(h => h.RuleKind == RuleFailureTag.TaskRule && h.RuleId == ruleId));

    public Task<List<RuleFailure>> FailuresAsync(long ruleId) =>
        Db(db => db.Set<RuleFailure>().AsNoTracking().Where(f => f.RuleId == ruleId).OrderBy(f => f.FailedUtc).ToListAsync());

    public Task<List<OpsIncident>> IncidentsAsync(string code) =>
        Db(db => db.Set<OpsIncident>().AsNoTracking().Where(i => i.Code == code).OrderBy(i => i.Id).ToListAsync());

    public Task<List<Notification>> PauseNotificationsAsync() =>
        Db(db => db.Set<Notification>().AsNoTracking().Where(n => n.Kind == NotificationKinds.OpsRulePaused).OrderBy(n => n.Id).ToListAsync());

    public Task<List<Notification>> NotificationsForAsync(string userId) =>
        Db(db => db.Set<Notification>().AsNoTracking().Where(n => n.UserId == userId).OrderBy(n => n.Id).ToListAsync());

    /// <summary>
    /// 以別的使用者（要先 <see cref="GrantDesigner"/>）改規則：內容照 <see cref="CreateRuleAsync"/> 的預設，只改名稱，最後修改者換成那個人。
    /// </summary>
    public async Task UpdateRuleAsAsync(HttpClient client, string key)
    {
        var res = await client.PutAsJsonAsync($"{Base}/modules/{Module}/task-rules/{key}", new
        {
            key, name = new Dictionary<string, string> { ["zh-TW"] = key + " 改" }, active = true, repeat = "once", trigger = new { on = "onCreate" },
            tasks = new[] { new { code = "a", title = new Dictionary<string, string> { ["zh-TW"] = "跟進" }, assigneeFieldId = Owner.Id, dueOffsetDays = 0 } },
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    public Task<List<AuditLogEntry>> AuditAsync(string action) =>
        Db(db => db.Set<AuditLogEntry>().AsNoTracking().Where(a => a.Action == action).OrderBy(a => a.Id).ToListAsync());

    public Task<int> TaskCountAsync(long ruleId) => Db(db => db.Set<TaskItem>().CountAsync(t => t.RuleId == ruleId));

    public Task<List<string>> AdminsAsync() =>
        Db(async db => await db.Set<AppUser>().Where(u => u.IsActive && u.IsSystemAdmin).Select(u => u.UserId).ToListAsync());

    public async Task SendAsync(IRequest<ServiceResult> request)
    {
        await using var scope = App.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
        result.IsSuccess.Should().BeTrue(result.Message ?? result.Code);
    }
}
