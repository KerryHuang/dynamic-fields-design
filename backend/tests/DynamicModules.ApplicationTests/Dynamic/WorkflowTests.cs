using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Application.Dynamic.SchemaService.Data;
using DynamicModules.Application.Dynamic.SchemaService.Queries.GetModuleSchema;
using DynamicModules.Application.Dynamic.WorkflowService.Commands.DeleteWorkflow;
using DynamicModules.Application.Dynamic.WorkflowService.Commands.SaveWorkflow;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using DynamicModules.Application.Dynamic.WorkflowService.Queries.GetWorkflow;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>狀態機：定義驗證、新增只能從起始狀態、轉移與群組、唯讀狀態、刪除。</summary>
public class WorkflowTests : DesignerTestBase
{
    private sealed record World(long Status, long Note, long Draft, long Approved, long Closed, long SalesGroup, long ManagerGroup, long Title);

    private async Task<World> Build(bool save = true)
    {
        await NewModule("doc");
        var status = await MustAdd("doc", FieldType.Enum, label: "狀態");
        long Opt(string label) => ((OptionResponse)Send(new AddOptionRequest(status.Id, Text(label), null)).GetAwaiter().GetResult().Data!).Id;
        var draft = Opt("草稿");
        var approved = Opt("已核准");
        var closed = Opt("已結案");
        var note = await MustAdd("doc", FieldType.Text, label: "備註");
        await Send(new ActivateModuleRequest("doc"));
        var sales = Db.Context.Set<PermissionGroup>().Single(g => g.Key == "sales").Id;
        var manager = Db.Context.Set<PermissionGroup>().Single(g => g.Key == "sales-manager").Id;
        foreach (var g in new[] { sales, manager })
            Db.Context.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
            {
                GroupId = g, ProgramCode = (await Detail("doc")).ProgramCode, CanRead = true, CanAdd = true, CanEdit = true, CanDelete = true
            });
        await Db.Context.SaveChangesAsync();
        var w = new World(status.Id, note.Id, draft, approved, closed, sales, manager, (await Detail("doc")).TitleFieldId!.Value);
        if (save) (await Send(new SaveWorkflowRequest("doc", Input(w)))).IsSuccess.Should().BeTrue();
        return w;
    }

    /// <summary>草稿 →（業務主管）已核准（唯讀）→（不限群組）已結案（唯讀）。</summary>
    private static WorkflowInput Input(World w) => new(w.Status, w.Draft,
        [new WorkflowState(w.Draft), new WorkflowState(w.Approved, true), new WorkflowState(w.Closed, true)],
        [new WorkflowTransition(w.Draft, w.Approved, [w.ManagerGroup]), new WorkflowTransition(w.Approved, w.Closed, [])]);

    private static string K(long id) => $"f_{id}";
    private static string Opt(long id) => $"opt_{id}";

    private async Task<DynamicRecordResponse> Create(World w, long? status = null, string note = "初稿")
    {
        var values = new Dictionary<string, JsonElement?> { [K(w.Title)] = J("\"單\""), [K(w.Note)] = J($"\"{note}\"") };
        if (status is { } s) values[K(w.Status)] = J($"\"{Opt(s)}\"");
        var r = await Send(new CreateRecordRequest("doc", values));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (DynamicRecordResponse)r.Data!;
    }

    private async Task<DynamicRecordResponse> Reload(long id) => (DynamicRecordResponse)(await Send(new GetRecordRequest(id))).Data!;

    private Task<ServiceResult> Edit(DynamicRecordResponse rec, params (long Field, string Json)[] changes) =>
        Send(new UpdateRecordRequest(rec.Id, changes.ToDictionary(c => K(c.Field), c => (JsonElement?)J(c.Json)), rec.RowVersion));

    private static string StatusOf(World w, DynamicRecordResponse r) => r.Values[K(w.Status)]!.Value.GetString()!;

    // ───────── 定義驗證 ─────────

    [Fact]
    public async Task Save_Valid_RoundTrips()
    {
        var w = await Build();
        var got = (WorkflowResponse)(await Send(new GetWorkflowRequest("doc"))).Data!;
        got.StatusFieldId.Should().Be(w.Status);
        got.InitialOptionId.Should().Be(w.Draft);
        got.States.Should().HaveCount(3);
        got.Transitions.Should().HaveCount(2);
    }

    [Fact]
    public async Task Workflow_OldJson_ReadsWithDefaults()
    {
        var w = await Build();
        var id = Db.Context.Set<Workflow>().AsNoTracking().Single().Id;
        await Db.Context.Database.ExecuteSqlRawAsync(
            "UPDATE Dynamic_Workflow SET States = {0}, Transitions = {1} WHERE Id = {2}",
            $"[{{\"optionId\":{w.Draft},\"readOnly\":false}}]", $"[{{\"from\":{w.Draft},\"to\":{w.Approved},\"groupIds\":[]}}]", id);

        var wf = Db.Context.Set<Workflow>().AsNoTracking().Single(x => x.Id == id);

        wf.States.Should().ContainSingle().Which.Editable.Should().BeFalse();
        wf.Transitions.Should().ContainSingle().Which.Approval.Should().BeNull();
    }

    [Fact]
    public async Task Get_WithoutWorkflow_NotFound()
    {
        await Build(save: false);
        (await Send(new GetWorkflowRequest("doc"))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task Save_StatusFieldMustBeSingleEnum()
    {
        var w = await Build(save: false);
        var r = await Send(new SaveWorkflowRequest("doc", Input(w) with { StatusFieldId = w.Note }));
        r.IsSuccess.Should().BeFalse();
        r.Errors.Should().ContainKey("statusFieldId");
    }

    [Fact]
    public async Task Save_InitialMustBeAState()
    {
        var w = await Build(save: false);
        var r = await Send(new SaveWorkflowRequest("doc", Input(w) with { InitialOptionId = 999999 }));
        r.IsSuccess.Should().BeFalse();
        r.Errors.Should().ContainKey("initialOptionId");
    }

    [Fact]
    public async Task Save_StateMustBeAnOptionOfTheStatusField()
    {
        var w = await Build(save: false);
        var bad = Input(w) with { States = [new WorkflowState(w.Draft), new WorkflowState(424242)] };
        var r = await Send(new SaveWorkflowRequest("doc", bad));
        r.IsSuccess.Should().BeFalse();
        r.Errors.Should().ContainKey("states");
    }

    [Theory]
    [InlineData("unknownState")]
    [InlineData("selfLoop")]
    [InlineData("duplicate")]
    [InlineData("unknownGroup")]
    public async Task Save_BadTransition_Rejected(string kind)
    {
        var w = await Build(save: false);
        var t = kind switch
        {
            "unknownState" => new List<WorkflowTransition> { new(w.Draft, 777777, []) },
            "selfLoop" => [new(w.Draft, w.Draft, [])],
            "duplicate" => [new(w.Draft, w.Approved, []), new(w.Draft, w.Approved, [w.ManagerGroup])],
            _ => [new(w.Draft, w.Approved, [888888])]
        };
        var r = await Send(new SaveWorkflowRequest("doc", Input(w) with { Transitions = t }));
        r.IsSuccess.Should().BeFalse();
        r.Errors.Should().ContainKey("transitions");
    }

    [Fact]
    public async Task Save_DetailModuleCannotHaveWorkflow()
    {
        var w = await Build(save: false);
        await NewModule("line");
        await MustAdd("line", FieldType.Reference, RefTo("doc", ReferenceRelation.Detail), "表頭");
        var lineStatus = await MustAdd("line", FieldType.Enum, label: "狀態");
        var o = ((OptionResponse)(await Send(new AddOptionRequest(lineStatus.Id, Text("甲"), null))).Data!).Id;
        var r = await Send(new SaveWorkflowRequest("line", new WorkflowInput(lineStatus.Id, o, [new WorkflowState(o)], [])));
        r.IsSuccess.Should().BeFalse();
        w.Status.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Delete_RemovesTheWorkflow()
    {
        await Build();
        (await Send(new DeleteWorkflowRequest("doc"))).IsSuccess.Should().BeTrue();
        (await Send(new GetWorkflowRequest("doc"))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task StatusField_CannotBeDeactivatedWhileWorkflowExists()
    {
        var w = await Build();
        var r = await Deactivate(w.Status);
        r.IsSuccess.Should().BeFalse();
    }

    // ───────── 新增 ─────────

    [Fact]
    public async Task Create_WithoutStatus_StartsAtInitial()
    {
        var w = await Build();
        StatusOf(w, await Create(w)).Should().Be(Opt(w.Draft));
    }

    [Fact]
    public async Task Create_NonInitialStatus_Rejected()
    {
        var w = await Build();
        var r = await Send(new CreateRecordRequest("doc", new Dictionary<string, JsonElement?>
        {
            [K(w.Title)] = J("\"單\""), [K(w.Status)] = J($"\"{Opt(w.Approved)}\"")
        }));
        r.IsSuccess.Should().BeFalse();
        r.Errors.Should().ContainKey(K(w.Status));
    }

    // ───────── 轉移 ─────────

    [Fact]
    public async Task Transition_UserOutsideTheGroup_IsRejectedAndStateUnchanged()
    {
        var w = await Build();
        As("sales01");
        var rec = await Create(w);
        var r = await Edit(rec, (w.Status, $"\"{Opt(w.Approved)}\""));
        r.IsSuccess.Should().BeFalse();
        r.Errors.Should().ContainKey(K(w.Status));
        StatusOf(w, await Reload(rec.Id)).Should().Be(Opt(w.Draft));
    }

    [Fact]
    public async Task Transition_UserInTheGroup_Succeeds()
    {
        var w = await Build();
        As("sales01");
        var rec = await Create(w);
        As("manager01");
        var r = await Edit(rec, (w.Status, $"\"{Opt(w.Approved)}\""));
        r.IsSuccess.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        StatusOf(w, await Reload(rec.Id)).Should().Be(Opt(w.Approved));
    }

    [Fact]
    public async Task Transition_NotDefined_RejectedEvenForAdmin()
    {
        var w = await Build();
        var rec = await Create(w); // admin
        var r = await Edit(rec, (w.Status, $"\"{Opt(w.Closed)}\""));
        r.IsSuccess.Should().BeFalse();
        r.Errors.Should().ContainKey(K(w.Status));
    }

    [Fact]
    public async Task Transition_AdminMayDoDefinedTransitions()
    {
        var w = await Build();
        var rec = await Create(w);
        (await Edit(rec, (w.Status, $"\"{Opt(w.Approved)}\""))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Transition_EmptyGroupList_MeansAnyEditor()
    {
        var w = await Build();
        var rec = await Create(w);
        var approved = (DynamicRecordResponse)(await Edit(rec, (w.Status, $"\"{Opt(w.Approved)}\""))).Data!;
        As("sales01"); // 不在任何轉移群組，但 Approved → Closed 沒有限制群組
        var r = await Edit(approved, (w.Status, $"\"{Opt(w.Closed)}\""));
        r.IsSuccess.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
    }

    [Fact]
    public async Task Transition_SameStatusIsNotATransition()
    {
        var w = await Build();
        As("sales01");
        var rec = await Create(w);
        (await Edit(rec, (w.Status, $"\"{Opt(w.Draft)}\""), (w.Note, "\"改過\""))).IsSuccess.Should().BeTrue();
    }

    // ───────── 唯讀狀態 ─────────

    [Fact]
    public async Task ReadOnlyState_OtherFieldsCannotBeEdited()
    {
        var w = await Build();
        var rec = await Create(w);
        var approved = (DynamicRecordResponse)(await Edit(rec, (w.Status, $"\"{Opt(w.Approved)}\""))).Data!;
        var r = await Edit(approved, (w.Note, "\"偷改\""));
        r.IsSuccess.Should().BeTrue(); // 輸入被忽略，不算錯（與規則唯讀同語意）
        (await Reload(rec.Id)).Values[K(w.Note)]!.Value.GetString().Should().Be("初稿");
    }

    [Fact]
    public async Task ReadOnlyState_StatusStillTransitions()
    {
        var w = await Build();
        var rec = await Create(w);
        var approved = (DynamicRecordResponse)(await Edit(rec, (w.Status, $"\"{Opt(w.Approved)}\""))).Data!;
        var r = await Edit(approved, (w.Status, $"\"{Opt(w.Closed)}\""), (w.Note, "\"偷改\""));
        r.IsSuccess.Should().BeTrue(r.Message);
        var after = await Reload(rec.Id);
        StatusOf(w, after).Should().Be(Opt(w.Closed));
        after.Values[K(w.Note)]!.Value.GetString().Should().Be("初稿");
    }

    [Fact]
    public async Task ReadOnlyState_RecordCannotBeDeleted()
    {
        var w = await Build();
        var rec = await Create(w);
        var approved = (DynamicRecordResponse)(await Edit(rec, (w.Status, $"\"{Opt(w.Approved)}\""))).Data!;
        var r = await Send(new DeleteRecordRequest(rec.Id, approved.RowVersion));
        r.IsConflict.Should().BeTrue();
    }

    [Fact]
    public async Task EditableState_CanBeDeleted()
    {
        var w = await Build();
        var rec = await Create(w);
        (await Send(new DeleteRecordRequest(rec.Id, rec.RowVersion))).IsSuccess.Should().BeTrue();
    }

    // ───────── schema ─────────

    private async Task<SchemaWorkflow?> SchemaWorkflowFor(string user)
    {
        As(user);
        var r = await Send(new GetModuleSchemaRequest("doc"));
        r.IsSuccess.Should().BeTrue(r.Message);
        return ((ModuleSchemaResponse)r.Data!).Workflow;
    }

    [Fact]
    public async Task Schema_ListsOnlyTransitionsTheUserMayDo()
    {
        var w = await Build();
        var sales = await SchemaWorkflowFor("sales01");
        sales!.InitialState.Should().Be(Opt(w.Draft));
        sales.States.Single(x => x.Value == Opt(w.Approved)).ReadOnly.Should().BeTrue();
        sales.Transitions.Should().BeEquivalentTo([new SchemaWorkflowTransition(Opt(w.Approved), Opt(w.Closed))]);

        (await SchemaWorkflowFor("manager01"))!.Transitions.Should().HaveCount(2);
        (await SchemaWorkflowFor("admin"))!.Transitions.Should().HaveCount(2);
    }

    [Fact]
    public async Task Schema_WithoutWorkflow_HasNone()
    {
        await Build(save: false);
        (await SchemaWorkflowFor("admin")).Should().BeNull();
    }

    [Fact]
    public async Task Schema_EtagChangesWhenGroupMembershipChangesTransitions()
    {
        await Build();
        As("sales01");
        var before = ((ModuleSchemaResponse)(await Send(new GetModuleSchemaRequest("doc"))).Data!).PermHash;
        As("manager01");
        var after = ((ModuleSchemaResponse)(await Send(new GetModuleSchemaRequest("doc"))).Data!).PermHash;
        before.Should().NotBe(after);
    }
}
