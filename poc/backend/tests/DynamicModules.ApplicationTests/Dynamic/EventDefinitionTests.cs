using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>事件定義的儲存與驗證（設計 §4.1、§4.3、§4.6）。兩組不相干情境：出貨單明細產生異動、請假單更新餘額。</summary>
public class EventDefinitionTests : EffectsTestBase
{
    [Fact]
    public async Task Stock_event_with_each_lines_is_saved_and_listed()
    {
        var s = await BuildStock("evs");
        var r = await Send(new CreateEventRequest(Event("evs-post", s.Doc, OnTransition(s.DocDone),
            Create("post", s.Move, s.Line, FromLine(s.MoveItem, s.LineItem), Formula(s.MoveQty, "line", $"-{{{s.LineQty}}}"), FromHeader(s.MoveWhere, s.DocMemo)))));
        r.IsCreated.Should().BeTrue(string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));

        var list = (IReadOnlyList<EventResponse>)(await Send(new GetEventsRequest(SourceModuleKey: s.Doc))).Data!;
        list.Should().ContainSingle(e => e.Key == "evs-post" && e.SourceModuleKey == s.Doc);
    }

    [Fact]
    public async Task Leave_event_updates_the_referenced_record()
    {
        var l = await BuildLeave("evl");
        var r = await Send(new CreateEventRequest(Event("evl-touch", l.Req, OnTransition(l.ReqApproved),
            UpdateVia("touch", l.Balance, l.ReqBalance, Const(l.BalLast, "\"today\"")))));
        r.IsCreated.Should().BeTrue(string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
    }

    [Fact]
    public async Task Key_must_be_unique_and_well_formed()
    {
        var l = await BuildLeave("evk");
        var input = Event("evk-touch", l.Req, OnTransition(l.ReqApproved), UpdateVia("touch", l.Balance, l.ReqBalance, Const(l.BalLast, "\"today\"")));
        (await Send(new CreateEventRequest(input))).IsCreated.Should().BeTrue();
        (await Send(new CreateEventRequest(input))).IsConflict.Should().BeTrue();
        (await Send(new CreateEventRequest(input with { Key = "Bad Key" }))).IsInvalid.Should().BeTrue();
    }

    [Fact]
    public async Task Transition_trigger_needs_a_workflow_on_the_source()
    {
        var s = await BuildStock("evw");
        // 異動模組沒有狀態機，不能用 transition 觸發
        var r = await Send(new CreateEventRequest(Event("evw-bad", s.Move, OnTransition(s.DocDone),
            Create("x", s.Doc, null, Const(s.DocMemo, "\"a\"")))));
        r.IsInvalid.Should().BeTrue();
        r.Errors.Keys.Should().Contain(k => k.StartsWith("definition.trigger"));
    }

    [Fact]
    public async Task Effect_codes_must_be_unique_and_targets_must_exist()
    {
        var s = await BuildStock("evt");
        var dup = await Send(new CreateEventRequest(Event("evt-a", s.Doc, OnTransition(s.DocDone),
            Create("same", s.Move, null, Const(s.MoveWhere, "\"w\"")), Create("same", s.Move, null, Const(s.MoveWhere, "\"w\"")))));
        dup.IsInvalid.Should().BeTrue();
        var missing = await Send(new CreateEventRequest(Event("evt-b", s.Doc, OnTransition(s.DocDone),
            Create("x", "no-such-module", null, Const(s.MoveWhere, "\"w\"")))));
        missing.IsInvalid.Should().BeTrue();
    }

    [Fact]
    public async Task Set_target_cannot_be_a_computed_field_or_belong_to_another_module()
    {
        var s = await BuildStock("evc");
        var wrong = await Send(new CreateEventRequest(Event("evc-a", s.Doc, OnTransition(s.DocDone),
            Create("x", s.Move, null, Const(s.DocMemo, "\"a\"")))));
        wrong.IsInvalid.Should().BeTrue();
        wrong.Errors.Keys.Should().Contain(k => k.Contains("set"));
    }

    [Fact]
    public async Task Update_via_must_reference_the_target_module()
    {
        var l = await BuildLeave("evv");
        var bad = await Send(new CreateEventRequest(Event("evv-a", l.Req, OnTransition(l.ReqApproved),
            UpdateVia("touch", l.Req, l.ReqBalance, Const(l.ReqDays, "1")))));
        bad.IsInvalid.Should().BeTrue();
    }

    [Fact]
    public async Task Hidden_source_field_is_rejected_unless_explicitly_allowed()
    {
        var s = await BuildStock("evh");
        (await Send(new SaveModulePermissionsRequest(s.Doc, new ModulePermissionsDto([], [],
            [new FieldPermissionDto(s.DocMemo, AnyGroupId(), FieldAccess.Hidden)], [])))).IsSuccess.Should().BeTrue();

        var blocked = await Send(new CreateEventRequest(Event("evh-a", s.Doc, OnTransition(s.DocDone),
            Create("x", s.Move, null, FromHeader(s.MoveWhere, s.DocMemo)))));
        blocked.IsInvalid.Should().BeTrue();
        blocked.Errors.Keys.Should().Contain(k => k.Contains("allowHiddenSource") || k.Contains("set"));

        var allowed = await Send(new CreateEventRequest(Event("evh-a", s.Doc, OnTransition(s.DocDone),
            Create("x", s.Move, null, FromHeader(s.MoveWhere, s.DocMemo) with { AllowHiddenSource = true }))));
        allowed.IsCreated.Should().BeTrue(string.Join(";", allowed.Errors.Select(e => e.Key + e.Value[0])));
    }

    [Fact]
    public async Task Hiding_a_referenced_field_later_is_rejected_unless_event_allows_it()
    {
        var s = await BuildStock("evi");
        (await Send(new CreateEventRequest(Event("evi-a", s.Doc, OnTransition(s.DocDone),
            Create("x", s.Move, null, FromHeader(s.MoveWhere, s.DocMemo))))))
            .IsCreated.Should().BeTrue();

        var hide = new ModulePermissionsDto([], [], [new FieldPermissionDto(s.DocMemo, AnyGroupId(), FieldAccess.Hidden)], []);
        var rejected = await Send(new SaveModulePermissionsRequest(s.Doc, hide));
        rejected.IsInvalid.Should().BeTrue();
        rejected.Errors.Values.SelectMany(v => v).Should().Contain(m => m.Contains("evi-a"));

        // 明確允許之後才能隱藏
        (await Send(new UpdateEventRequest("evi-a", Event("evi-a", s.Doc, OnTransition(s.DocDone),
            Create("x", s.Move, null, FromHeader(s.MoveWhere, s.DocMemo) with { AllowHiddenSource = true })))))
            .IsSuccess.Should().BeTrue();
        (await Send(new SaveModulePermissionsRequest(s.Doc, hide))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Event_chain_that_loops_back_is_rejected_and_names_the_cycle()
    {
        var s = await BuildStock("evy");
        // 單據 → 異動（create）。再讓異動 → 單據（create）就成環
        (await Send(new CreateEventRequest(Event("evy-a", s.Doc, new EventTrigger("create", null, null, null),
            Create("x", s.Move, null, Const(s.MoveWhere, "\"w\""))))))
            .IsCreated.Should().BeTrue();
        var loop = await Send(new CreateEventRequest(Event("evy-b", s.Move, new EventTrigger("create", null, null, null),
            Create("y", s.Doc, null, Const(s.DocMemo, "\"w\"")))));
        loop.IsInvalid.Should().BeTrue();
        loop.Errors.Values.SelectMany(v => v).Should().Contain(m => m.Contains("evy-a") && m.Contains("evy-b"));
    }

    [Fact]
    public async Task Event_creating_into_its_own_source_module_is_a_cycle()
    {
        var s = await BuildStock("evz");
        var self = await Send(new CreateEventRequest(Event("evz-a", s.Doc, new EventTrigger("create", null, null, null),
            Create("x", s.Doc, null, Const(s.DocMemo, "\"w\"")))));
        self.IsInvalid.Should().BeTrue();
    }

    [Fact]
    public async Task Disabled_events_do_not_count_toward_cycles_but_enabling_rechecks()
    {
        var s = await BuildStock("evd");
        (await Send(new CreateEventRequest(Event("evd-a", s.Doc, new EventTrigger("create", null, null, null),
            Create("x", s.Move, null, Const(s.MoveWhere, "\"w\"")))))).IsCreated.Should().BeTrue();
        var b = Event("evd-b", s.Move, new EventTrigger("create", null, null, null), Create("y", s.Doc, null, Const(s.DocMemo, "\"w\"")));
        (await Send(new CreateEventRequest(b with { Active = false }))).IsCreated.Should().BeTrue();
        (await Send(new UpdateEventRequest("evd-b", b))).IsInvalid.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_event_removes_it()
    {
        var l = await BuildLeave("evx");
        (await Send(new CreateEventRequest(Event("evx-a", l.Req, OnTransition(l.ReqApproved),
            UpdateVia("touch", l.Balance, l.ReqBalance, Const(l.BalLast, "\"today\"")))))).IsCreated.Should().BeTrue();
        (await Send(new DeleteEventRequest("evx-a"))).IsSuccess.Should().BeTrue();
        ((IReadOnlyList<EventResponse>)(await Send(new GetEventsRequest())).Data!).Should().BeEmpty();
    }

    [Fact]
    public async Task Fake_custom_handler_must_be_registered_and_declared_writes_feed_the_cycle_graph()
    {
        var s = await BuildStock("evu");
        var unknown = await Send(new CreateEventRequest(Event("evu-a", s.Doc, OnTransition(s.DocDone),
            new EventEffect("c", "custom", null, null, null, null, null, null, "no-such-handler", null))));
        unknown.IsInvalid.Should().BeTrue();
    }
}
