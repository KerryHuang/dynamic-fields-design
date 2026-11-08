using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>事件定義驗證的漏洞回歸（第一輪實作審查 a）：停用欄位繞過隱藏守衛、複製彙總值、公式自環、today 常數。</summary>
public class EventDefinitionHardeningTests : EffectsTestBase
{
    private static string Messages(ServiceResult r) => string.Join(";", r.Errors.Select(e => e.Key + "=" + string.Join("/", e.Value)));

    [Fact]
    public async Task Hiding_a_deactivated_field_that_an_event_formula_reads_is_still_rejected()
    {
        var s = await BuildStock("hdf");
        var amount = await MustAdd(s.Doc, FieldType.Decimal, label: "金額");
        (await Send(new CreateEventRequest(Event("hdf-a", s.Doc, OnTransition(s.DocDone),
            Create("x", s.Move, null, Formula(s.MoveQty, "header", $"{{{amount.Id}}}"))))))
            .IsCreated.Should().BeTrue();

        // 先停用，公式解析不到欄位；隱藏權限不能因此被放行（之後重新啟用，隱藏值就流進目標）
        (await Deactivate(amount.Id)).IsSuccess.Should().BeTrue();
        var hide = new ModulePermissionsDto([], [], [new FieldPermissionDto(amount.Id, AnyGroupId(), FieldAccess.Hidden)], []);
        var rejected = await Send(new SaveModulePermissionsRequest(s.Doc, hide));

        rejected.IsInvalid.Should().BeTrue(Messages(rejected));
        rejected.Errors.Values.SelectMany(v => v).Should().Contain(m => m.Contains("hdf-a"));
    }

    [Fact]
    public async Task A_set_cannot_copy_a_rollup_or_a_formula_that_depends_on_one()
    {
        var s = await BuildStock("rol");
        var total = await MustAdd(s.Doc, FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(s.LineHead, RollupFunction.Sum, s.LineQty, null) }, "合計");
        var doubled = await MustAdd(s.Doc, FieldType.Formula, new FieldConfig { Formula = $"{{{total.Id}}} * 2", ResultType = "number" }, "加倍");

        var fromRollup = await Send(new CreateEventRequest(Event("rol-a", s.Doc, OnTransition(s.DocDone),
            Create("x", s.Move, null, FromHeader(s.MoveQty, total.Id)))));
        fromRollup.IsInvalid.Should().BeTrue();
        Messages(fromRollup).Should().Contain("彙總");

        var fromFormula = await Send(new CreateEventRequest(Event("rol-b", s.Doc, OnTransition(s.DocDone),
            Create("x", s.Move, null, FromHeader(s.MoveQty, doubled.Id)))));
        fromFormula.IsInvalid.Should().BeTrue();

        var inFormula = await Send(new CreateEventRequest(Event("rol-c", s.Doc, OnTransition(s.DocDone),
            Create("x", s.Move, null, Formula(s.MoveQty, "header", $"{{{total.Id}}} + 1")))));
        inFormula.IsInvalid.Should().BeTrue();
    }

    [Fact]
    public async Task An_update_that_sets_the_input_of_a_watched_formula_on_itself_is_a_cycle()
    {
        await NewModule("fcy", activate: false);
        var x = await MustAdd("fcy", FieldType.Decimal, label: "x");
        var f = await MustAdd("fcy", FieldType.Formula, new FieldConfig { Formula = $"{{{x.Id}}}", ResultType = "number" }, "F");
        var me = await MustAdd("fcy", FieldType.Reference, RefTo("fcy"), "自己");
        (await Send(new DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule.ActivateModuleRequest("fcy"))).IsSuccess.Should().BeTrue();

        var r = await Send(new CreateEventRequest(Event("fcy-a", "fcy", new EventTrigger("update", null, null, [f.Id]),
            UpdateVia("bump", "fcy", me.Id, Formula(x.Id, "header", $"{{{f.Id}}} + 1")))));

        r.IsInvalid.Should().BeTrue(Messages(r));
        r.Errors.Values.SelectMany(v => v).Should().Contain(m => m.Contains("fcy-a"));
    }

    [Fact]
    public async Task Const_today_is_only_accepted_for_date_fields()
    {
        var s = await BuildStock("tod");
        var stamp = await MustAdd(s.Move, FieldType.DateTime, label: "時間");
        var r = await Send(new CreateEventRequest(Event("tod-a", s.Doc, OnTransition(s.DocDone),
            Create("x", s.Move, null, Const(stamp.Id, "\"today\"")))));
        r.IsInvalid.Should().BeTrue(Messages(r));
    }
}
