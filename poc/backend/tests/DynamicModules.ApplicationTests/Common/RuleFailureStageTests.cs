using System.Reflection;
using System.Text.Json;
using DynamicModules.Application.Common.Observability;
using DynamicModules.Application.Dynamic.CheckService;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.TaskRuleService;
using DynamicModules.ApplicationTests.Dynamic;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DynamicModules.ApplicationTests.Common;

/// <summary>
/// 三個寫入後階段（事件、跨記錄檢查、任務規則）在規則內部失敗時，例外要帶規則歸屬標記（J9）。
/// 刪掉階段裡的 <c>catch … when (RuleFailureTag.Mark…)</c> 這些測試就會失敗。
/// 做法：在規則執行途中才會呼叫的一個 repository 方法上注入例外，從請求外面讀標記。
/// </summary>
public class RuleFailureStageTests : EffectsTestBase
{
    protected override bool ScopePerRequest => true;

    /// <summary>目前要注入例外的階段類別名；只有從該階段的 RunAsync 呼叫出來的 repository 方法才會丟（建立記錄等前段流程也讀同一個方法）。</summary>
    private string? _stage;

    private bool InStage(string only) => _stage == only && Environment.StackTrace.Contains(only + ".RunAsync");

    /// <summary>包住真實 repository，armed 時指定的方法丟例外；其他呼叫照常轉給真實物件。</summary>
    public class ThrowOn<T> : DispatchProxy where T : class
    {
        public T Inner = null!;
        public string Method = "";
        public Func<bool> Armed = () => false;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (Armed() && targetMethod!.Name == Method) throw new InvalidOperationException("injected");
            try { return targetMethod!.Invoke(Inner, args); }
            catch (TargetInvocationException e) { throw e.InnerException!; }
        }
    }

    private void Wrap<T>(IServiceCollection services, string method, params string[] stages) where T : class
    {
        var descriptor = services.Single(d => d.ServiceType == typeof(T));
        services.Remove(descriptor);
        services.AddScoped<T>(sp =>
        {
            var proxy = DispatchProxy.Create<T, ThrowOn<T>>();
            var p = (ThrowOn<T>)(object)proxy;
            p.Inner = (T)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!);
            p.Method = method;
            p.Armed = () => stages.Any(InStage);
            return proxy;
        });
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        // 事件的 when 條件與檢查的 where 條件，都在規則內部才會讀有效使用者清單
        Wrap<IModuleRepository>(services, nameof(IModuleRepository.ListUserIdsAsync), nameof(EventStage), nameof(CheckStage));
        Wrap<ITaskRuleRepository>(services, nameof(ITaskRuleRepository.GetLatestOccurrenceAsync), nameof(TaskRuleStage));
    }

    private JsonElement WhereMoveIs(Stock s) => J($$"""{"all":[{"field":{{s.MoveWhere}},"op":"eq","value":"w1"}]}""");

    private async Task<InvalidOperationException> CreateMoveAndCatch(Stock s, string stage)
    {
        var values = new Dictionary<string, JsonElement?>
        {
            [K(s.MoveTitle)] = J("\"t\""), [K(s.MoveItem)] = J("\"A\""), [K(s.MoveQty)] = J("1"), [K(s.MoveWhere)] = J("\"w1\"")
        };
        _stage = stage;
        try { await Send(new CreateRecordRequest(s.Move, values)); }
        catch (InvalidOperationException e) { return e; }
        finally { _stage = null; }
        throw new Xunit.Sdk.XunitException("沒有丟出例外");
    }

    [Fact]
    public async Task EventStage_MarksEvent()
    {
        var s = await BuildStock("rfe");
        var r = await Send(new CreateEventRequest(new EventInput("rfe-ev", Text("ev"), s.Move, true,
            new EventDefinition(new EventTrigger("create", null, null, null), WhereMoveIs(s),
                [Create("x", s.Doc, null, Const(s.DocMemo, "\"m\""))]))));
        r.IsCreated.Should().BeTrue(string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        var id = Db.Context.Set<ModuleEvent>().Single(e => e.Key == "rfe-ev").Id;

        var e = await CreateMoveAndCatch(s, nameof(EventStage));

        RuleFailureTag.Read(e).Should().Be((RuleFailureTag.Event, id));
    }

    [Fact]
    public async Task CheckStage_MarksCheck()
    {
        var s = await BuildStock("rfc");
        var def = new CheckDefinition([s.MoveItem], "sum", s.MoveQty, WhereMoveIs(s),
            new CheckAssert("gte", new CheckLimit(0, null, null)), Text("不足"));
        var r = await Send(new CreateCheckRequest(new CheckInput("rfc-min", Text("min"), s.Move, true, def)));
        r.IsCreated.Should().BeTrue(string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        var id = Db.Context.Set<ModuleCheck>().Single(c => c.Key == "rfc-min").Id;

        var e = await CreateMoveAndCatch(s, nameof(CheckStage));

        RuleFailureTag.Read(e).Should().Be((RuleFailureTag.Check, id));
    }

    [Fact]
    public async Task TaskRuleStage_MarksTaskRule()
    {
        await NewModule("rft");
        var user = await MustAdd("rft", FieldType.User, label: "人員");
        (await Send(new ActivateModuleRequest("rft"))).IsSuccess.Should().BeTrue();
        var title = (await Detail("rft")).TitleFieldId!.Value;
        var input = new TaskRuleInput("rft-rule", Text("rule"), true, TaskRuleRepeat.Once, new TaskRuleTriggerInput("onCreate", null, null), null,
            [new TaskRuleTaskInput("t1", Text("任務"), null, user.Id, null, 0, null)]);
        var created = await Send(new CreateTaskRuleRequest("rft", input));
        created.IsCreated.Should().BeTrue(string.Join(";", created.Errors.Select(e => e.Key + e.Value[0])));
        var id = Db.Context.Set<TaskRule>().Single(t => t.Key == "rft-rule").Id;

        _stage = nameof(TaskRuleStage);
        InvalidOperationException? caught = null;
        try { await Send(new CreateRecordRequest("rft", new Dictionary<string, JsonElement?> { [K(title)] = J("\"a\""), [K(user.Id)] = J("\"sales01\"") })); }
        catch (InvalidOperationException e) { caught = e; }
        finally { _stage = null; }

        caught.Should().NotBeNull();
        RuleFailureTag.Read(caught!).Should().Be((RuleFailureTag.TaskRule, id));
    }
}
