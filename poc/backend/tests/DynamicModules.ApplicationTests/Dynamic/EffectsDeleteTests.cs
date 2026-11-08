using DynamicModules.Application.Dynamic.CheckService;
using DynamicModules.Application.Dynamic.DeleteService.Data;
using DynamicModules.Application.Dynamic.DeleteService.Queries.CheckDelete;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Application.Dynamic.LedgerService;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>安全刪除要認得事件、跨記錄檢查、分錄設定的引用（外鍵是 Restrict，不盤點會變成資料庫錯誤）。</summary>
public class EffectsDeleteTests : EffectsTestBase
{
    private async Task<DeleteCheckResponse> Check(DeleteTarget target, string? key, long id) =>
        (DeleteCheckResponse)(await Send(new CheckDeleteRequest(target, key, id))).Data!;

    [Fact]
    public async Task Module_and_fields_used_by_an_event_are_blocked()
    {
        var l = await BuildLeave("dle");
        (await Send(new CreateEventRequest(Event("dle-a", l.Req, OnTransition(l.ReqApproved),
            UpdateVia("touch", l.Balance, l.ReqBalance, Const(l.BalLast, "\"today\"")))))).IsCreated.Should().BeTrue();
        (await Check(DeleteTarget.Module, l.Balance, 0)).Blockers.Should().Contain(b => b.Kind == DeleteBlockerKind.Event);
        (await Check(DeleteTarget.Field, null, l.BalLast)).Blockers.Should().Contain(b => b.Kind == DeleteBlockerKind.Event);
        (await Check(DeleteTarget.Field, null, l.ReqDays)).Blockers.Should().NotContain(b => b.Kind == DeleteBlockerKind.Event);
    }

    [Fact]
    public async Task Fields_and_module_of_a_check_are_blocked()
    {
        var s = await BuildStock("dlc");
        var def = new CheckDefinition([s.MoveItem], "sum", s.MoveQty, null, new CheckAssert("gte", new CheckLimit(0, null, null)), Text("不足"));
        (await Send(new CreateCheckRequest(new CheckInput("dlc-a", Text("a"), s.Move, true, def)))).IsCreated.Should().BeTrue();
        (await Check(DeleteTarget.Field, null, s.MoveQty)).Blockers.Should().Contain(b => b.Kind == DeleteBlockerKind.Check);
        (await Check(DeleteTarget.Field, null, s.MoveWhere)).Blockers.Should().NotContain(b => b.Kind == DeleteBlockerKind.Check);
        (await Check(DeleteTarget.Module, s.Move, 0)).Blockers.Should().Contain(b => b.Kind == DeleteBlockerKind.Check);
    }

    [Fact]
    public async Task Ledger_module_and_reversal_field_are_blocked()
    {
        var s = await BuildStock("dll");
        (await Send(new SaveLedgerRequest(s.Move, new LedgerInput(s.MoveReversal, [s.MoveQty], [], false)))).IsSuccess.Should().BeTrue();
        (await Check(DeleteTarget.Field, null, s.MoveReversal)).Blockers.Should().Contain(b => b.Kind == DeleteBlockerKind.Ledger);
        (await Check(DeleteTarget.Module, s.Move, 0)).Blockers.Should().Contain(b => b.Kind == DeleteBlockerKind.Ledger);
    }
}
