using System.Text.Json;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>流程圖格式的定義驗證（流程圖設計 §9 G6 語法、G9、G10、G13 政策）；fg1 期間 graph 一律再加一則 shape 錯誤（計畫 J9）。</summary>
public class ApprovalGraphDefinitionValidatorTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private PurchaseRig _r = null!;

    private async Task<Dictionary<string, string[]>> Validate(ApprovalDefinition d, ApprovalDefinition? previous = null)
    {
        _r ??= await BuildPurchase();
        using var scope = NewScope();
        var module = (await scope.ServiceProvider.GetRequiredService<IModuleRepository>().GetByKeyAsync(_r.Module, includeChildren: true, CancellationToken.None))!;
        return await scope.ServiceProvider.GetRequiredService<ApprovalDefinitionValidator>()
            .ValidateAsync(module, module.Workflow!, d, previous, CancellationToken.None);
    }

    private static string E(Dictionary<string, string[]> errors, string key)
    {
        errors.Should().ContainKey(key, string.Join("；", errors.Select(e => $"{e.Key}={e.Value[0]}")));
        return errors[key][0];
    }

    private static ApprovalNodeDef Approval(string id, ApproverSource a, string name, List<long>? editable = null, int? due = null) =>
        new(id, ApprovalNodeKinds.Approval, Text(name), Approvers: a, Mode: ApprovalModes.Any, EditableFields: editable ?? [], DueHours: due, OnEmpty: ApprovalOnEmpty.Fallback);

    /// <summary>流程圖設計 §7.1 的範例，換成測試組織的群組與欄位。</summary>
    private ApprovalDefinition Design() => new(ApprovalShapes.Graph, null, null, null, [],
    [
        new ApprovalNodeDef("start", ApprovalNodeKinds.Start),
        Approval("mgr", Manager(1), "直屬主管", due: 24),
        new ApprovalNodeDef("amount", ApprovalNodeKinds.Condition, Text("金額分級")),
        new ApprovalNodeDef("split", ApprovalNodeKinds.Fork, Text("法務與財務")),
        Approval("legal", Group(FinanceGroup), "法務", due: 48),
        Approval("fin", Group(PurchaseLeadGroup), "財務", editable: [_r.DueDate], due: 48),
        new ApprovalNodeDef("merge", ApprovalNodeKinds.Join, ForkId: "split", Mode: ApprovalModes.All),
        new ApprovalNodeDef("tell-hr", ApprovalNodeKinds.Notify, Text("知會人事"),
            Recipients: [new(ApprovalNotifyRecipientKinds.Group, GroupIds: [BoardGroup]), new(ApprovalNotifyRecipientKinds.Initiator)],
            Template: new NotifyTemplateDef(Text("{title} 已過主管核准"), Text($"申請金額 {{f:{_r.Total}}}，請備查。"))),
        new ApprovalNodeDef("end", ApprovalNodeKinds.End)
    ],
    [
        new ApprovalEdgeDef("e1", "start", "mgr"), new ApprovalEdgeDef("e2", "mgr", "amount"),
        new ApprovalEdgeDef("e3", "amount", "split", Text("十萬以上"), Cond(_r.Total, "gte", 100000), 1),
        new ApprovalEdgeDef("e4", "amount", "tell-hr", Text("其他"), Default: true),
        new ApprovalEdgeDef("e5", "split", "legal"), new ApprovalEdgeDef("e6", "split", "fin"),
        new ApprovalEdgeDef("e7", "legal", "merge"), new ApprovalEdgeDef("e8", "fin", "merge"),
        new ApprovalEdgeDef("e9", "merge", "tell-hr"), new ApprovalEdgeDef("e10", "tell-hr", "end")
    ]);

    private async Task<ApprovalDefinition> DesignAsync()
    {
        _r ??= await BuildPurchase();
        return Design();
    }

    private static ApprovalDefinition Mod(ApprovalDefinition d, string id, Func<ApprovalNodeDef, ApprovalNodeDef> f) =>
        d with { Nodes = [.. d.Nodes!.Select(n => n.Id == id ? f(n) : n)] };

    private static ApprovalDefinition ModEdge(ApprovalDefinition d, string id, Func<ApprovalEdgeDef, ApprovalEdgeDef> f) =>
        d with { Edges = [.. d.Edges!.Select(e => e.Id == id ? f(e) : e)] };

    private static ApprovalNodeDef Notify(ApprovalNodeDef n, Func<NotifyTemplateDef, NotifyTemplateDef> f) => n with { Template = f(n.Template!) };

    [Fact]
    public async Task Graph_DesignExample_OnlyShapeGateError()
    {
        var errors = await Validate(await DesignAsync());
        errors.Keys.Should().Equal("shape");
        errors["shape"][0].Should().Be(ApprovalMessages.GraphNotAccepted);
    }

    [Fact]
    public async Task Shape_Unknown_StillShapeInvalid()
    {
        var d = await DesignAsync();
        E(await Validate(d with { Shape = "mesh" }), "shape").Should().Be(ApprovalMessages.ShapeInvalid);
    }

    [Fact]
    public async Task Graph_TopLevelAttributes_SameAsSequence()
    {
        var d = await DesignAsync();
        var errors = await Validate(d with { RejectTo = 999999, FallbackApprovers = Manager(6) });
        E(errors, "rejectTo").Should().Be(ApprovalMessages.RejectToInvalidState);
        E(errors, "fallbackApprovers").Should().Be(ApprovalMessages.ManagerLevel(5));
    }

    [Fact]
    public async Task Graph_ApprovalNodeAttributes_KeyedByNodeId()
    {
        var d = await DesignAsync();
        d = Mod(d, "mgr", n => n with { Mode = "x", DueHours = 0 });
        d = Mod(d, "legal", n => n with { Approvers = Group(999999) });
        d = Mod(d, "fin", n => n with { EditableFields = [999999] });
        var errors = await Validate(d);
        E(errors, "nodes.mgr.mode").Should().Be(ApprovalMessages.ModeInvalid);
        E(errors, "nodes.mgr.dueHours").Should().Contain("1 到");
        E(errors, "nodes.legal.approvers").Should().Be(ApprovalMessages.GroupsInvalid);
        E(errors, "nodes.fin.editableFields").Should().Be(ApprovalMessages.EditableFieldInvalid);
        errors.Keys.Should().NotContain(k => k.StartsWith("stages"));
    }

    [Fact]
    public async Task Graph_ApprovalSkipCondition_InvalidField_KeyedByNodeId()
    {
        var d = Mod(await DesignAsync(), "mgr", n => n with { When = Cond(999999, "gte", 1) });
        E(await Validate(d), "nodes.mgr.when").Should().StartWith("條件不正確");
    }

    [Fact]
    public async Task Graph_ConditionEdgeWhenInvalid_KeyedByEdgeId()
    {
        var d = ModEdge(await DesignAsync(), "e3", e => e with { When = Cond(999999, "gte", 1), Label = new I18nText([KeyValuePair.Create("en-US", "x")]) });
        var errors = await Validate(d);
        E(errors, "edges.e3.when").Should().StartWith("條件不正確");
        E(errors, "edges.e3.label").Should().Be(ApprovalMessages.EdgeLabelInvalid);
    }

    [Fact]
    public async Task Graph_NotifyRecipients_InactiveUserOrMissingGroup_400()
    {
        var d = await DesignAsync();
        Db.Context.Add(new AppUser { UserId = "left", DisplayName = "left", IsActive = false });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();

        E(await Validate(Mod(d, "tell-hr", n => n with { Recipients = [new(ApprovalNotifyRecipientKinds.Users, UserIds: ["left"])] })),
            "nodes.tell-hr.recipients").Should().Be(ApprovalMessages.UsersInvalid);
        E(await Validate(Mod(d, "tell-hr", n => n with { Recipients = [new(ApprovalNotifyRecipientKinds.Group, GroupIds: [999999])] })),
            "nodes.tell-hr.recipients").Should().Be(ApprovalMessages.GroupsInvalid);
        E(await Validate(Mod(d, "tell-hr", n => n with { Recipients = [new(ApprovalNotifyRecipientKinds.Initiator, GroupIds: [BoardGroup])] })),
            "nodes.tell-hr.recipients").Should().Be(ApprovalMessages.NotifyRecipientInvalid);
        E(await Validate(Mod(d, "tell-hr", n => n with { Recipients = [new("everyone")] })),
            "nodes.tell-hr.recipients").Should().Be(ApprovalMessages.NotifyRecipientInvalid);
    }

    [Fact]
    public async Task Graph_NotifyRecipients_CountOutOfRange_400()
    {
        var d = await DesignAsync();
        var max = ApprovalGraphLimits.MaxNotifyRecipients;
        E(await Validate(Mod(d, "tell-hr", n => n with { Recipients = [] })), "nodes.tell-hr.recipients").Should().Be(ApprovalMessages.NotifyRecipientsCount(max));
        E(await Validate(Mod(d, "tell-hr", n => n with { Recipients = [.. Enumerable.Range(0, max + 1).Select(_ => new NotifyRecipientDef(ApprovalNotifyRecipientKinds.Initiator))] })),
            "nodes.tell-hr.recipients").Should().Be(ApprovalMessages.NotifyRecipientsCount(max));
        (await Validate(Mod(d, "tell-hr", n => n with { Recipients = [.. Enumerable.Range(0, max).Select(_ => new NotifyRecipientDef(ApprovalNotifyRecipientKinds.Initiator))] })))
            .Keys.Should().Equal("shape");
    }

    [Fact]
    public async Task Graph_NotifyTemplate_UnknownPlaceholder_400()
    {
        var d = Mod(await DesignAsync(), "tell-hr", n => Notify(n, t => t with { Body = Text("請 {approver} 確認") }));
        E(await Validate(d), "nodes.tell-hr.template.body").Should().Be(ApprovalMessages.TemplatePlaceholderUnknown("{approver}"));

        // 單獨的大括號照字面，不是錯誤（U8）
        d = Mod(d, "tell-hr", n => Notify(n, t => t with { Body = Text("收到 :-}") }));
        (await Validate(d)).Keys.Should().Equal("shape");
    }

    [Fact]
    public async Task Graph_NotifyTemplate_DetailOrInactiveField_400()
    {
        var d = await DesignAsync();
        E(await Validate(Mod(d, "tell-hr", n => Notify(n, t => t with { Body = Text($"數量 {{f:{_r.LineQty}}}") }))),
            "nodes.tell-hr.template.body").Should().Be(ApprovalMessages.TemplateFieldInvalid);

        var off = await MustAdd(_r.Module, FieldType.Text, label: "停用中");
        (await Deactivate(off.Id)).IsSuccess.Should().BeTrue();
        E(await Validate(Mod(d, "tell-hr", n => Notify(n, t => t with { Title = Text($"{{f:{off.Id}}}") }))),
            "nodes.tell-hr.template.title").Should().Be(ApprovalMessages.TemplateFieldInvalid);
    }

    [Fact]
    public async Task Graph_NotifyTemplate_TitleTooLong_400()
    {
        var d = await DesignAsync();
        var title = ApprovalGraphLimits.MaxTemplateTitleLength;
        E(await Validate(Mod(d, "tell-hr", n => Notify(n, t => t with { Title = Text(new string('字', title + 1)) }))),
            "nodes.tell-hr.template.title").Should().Be(ApprovalMessages.TemplateTooLong(title));
        (await Validate(Mod(d, "tell-hr", n => Notify(n, t => t with { Title = Text(new string('字', title)) })))).Keys.Should().Equal("shape");
        E(await Validate(Mod(d, "tell-hr", n => Notify(n, t => t with { Body = Text(new string('字', ApprovalGraphLimits.MaxTemplateBodyLength + 1)) }))),
            "nodes.tell-hr.template.body").Should().Be(ApprovalMessages.TemplateTooLong(ApprovalGraphLimits.MaxTemplateBodyLength));
    }

    [Fact]
    public async Task Graph_NotifyTemplate_MissingOrNoChinese_400()
    {
        var d = await DesignAsync();
        var errors = await Validate(Mod(d, "tell-hr", n => n with { Template = null }));
        E(errors, "nodes.tell-hr.template.title").Should().Be(ApprovalMessages.TemplateRequired);
        E(errors, "nodes.tell-hr.template.body").Should().Be(ApprovalMessages.TemplateRequired);
        E(await Validate(Mod(d, "tell-hr", n => Notify(n, t => t with { Body = new I18nText([KeyValuePair.Create("en-US", "x")]) }))),
            "nodes.tell-hr.template.body").Should().Be(ApprovalMessages.TemplateRequired);
    }

    [Fact]
    public async Task Graph_NotifyManagerLevel_OverPolicy_400()
    {
        var d = await DesignAsync();
        E(await Validate(Mod(d, "tell-hr", n => n with { Recipients = [new(ApprovalNotifyRecipientKinds.InitiatorManager, Level: 6)] })),
            "nodes.tell-hr.recipients").Should().Be(ApprovalMessages.ManagerLevel(5));
        // 沒填層數照設計 §12.1 預設第 1 層（再驗政策上限）
        (await Validate(Mod(d, "tell-hr", n => n with { Recipients = [new(ApprovalNotifyRecipientKinds.InitiatorManager)] }))).Keys.Should().Equal("shape");
        (await Validate(Mod(d, "tell-hr", n => n with { Recipients = [new(ApprovalNotifyRecipientKinds.InitiatorManager, Level: 5)] }))).Keys.Should().Equal("shape");
    }

    [Fact]
    public async Task Graph_NotifyManagerLevel_KeepOldApplies()
    {
        var d = await DesignAsync();
        var old = Mod(d, "tell-hr", n => n with { Recipients = [new(ApprovalNotifyRecipientKinds.InitiatorManager, Level: 6)] });
        (await Validate(old, old)).Keys.Should().Equal("shape");
        var higher = Mod(d, "tell-hr", n => n with { Recipients = [new(ApprovalNotifyRecipientKinds.InitiatorManager, Level: 7)] });
        E(await Validate(higher, old), "nodes.tell-hr.recipients").Should().Contain("沿用原本的數字");
    }

    [Fact]
    public async Task Graph_NameRules_ApprovalRequired_ForkOptional()
    {
        var d = await DesignAsync();
        // 分支、條件、匯合名稱可以省略（U6）
        var noOptional = Mod(Mod(Mod(d, "split", n => n with { Name = null }), "amount", n => n with { Name = null }), "merge", n => n with { Name = null });
        (await Validate(noOptional)).Keys.Should().Equal("shape");

        var errors = await Validate(Mod(Mod(Mod(d, "mgr", n => n with { Name = null }), "tell-hr", n => n with { Name = null }),
            "split", n => n with { Name = new I18nText([KeyValuePair.Create("en-US", "x")]) }));
        E(errors, "nodes.mgr.name").Should().Be(ApprovalMessages.NodeNameRequired);
        E(errors, "nodes.tell-hr.name").Should().Be(ApprovalMessages.NodeNameRequired);
        E(errors, "nodes.split.name").Should().Be(ApprovalMessages.NodeNameRequired);
    }

    [Fact]
    public async Task Graph_StructuralErrors_MappedToMessages()
    {
        var d = await DesignAsync();
        // x2 沒有入線、x3 只能從 x2 到達：x3 是「無法從開始到達」
        d = d with
        {
            Nodes = [.. d.Nodes!, Approval("x2", Manager(1), "x2"), Approval("x3", Manager(1), "x3")],
            Edges = [.. d.Edges!, new("e11", "x2", "x3"), new("e12", "x3", "end")]
        };
        var errors = await Validate(d);
        E(errors, "nodes.x3").Should().Be(ApprovalMessages.Graph("unreachable"));
        E(errors, "shape").Should().Be(ApprovalMessages.GraphNotAccepted);
    }

    /// <summary>不合法並行區（共享案例 AG-090 的圖）：存檔驗證要回 G7／G8 錯誤，不能因警告分析無限遞迴而讓程序終止。</summary>
    [Fact]
    public async Task Graph_InvalidParallelRegion_ReturnsG7G8Errors_NoCrash()
    {
        ApprovalNodeDef N(string id) => new(id, ApprovalNodeKinds.Notify, Text(id), Recipients: [new(ApprovalNotifyRecipientKinds.Initiator)]);
        ApprovalEdgeDef E1(string id, string from, string to) => new(id, from, to);
        var d = new ApprovalDefinition(ApprovalShapes.Graph, null, null, null, [],
        [
            new("s", ApprovalNodeKinds.Start), new("f1", ApprovalNodeKinds.Fork), new("f2", ApprovalNodeKinds.Fork),
            N("n1"), N("n2"), N("n4"), Approval("a", Manager(1), "a"),
            new("j1", ApprovalNodeKinds.Join, ForkId: "f1", Mode: ApprovalModes.All),
            new("j2", ApprovalNodeKinds.Join, ForkId: "f2", Mode: ApprovalModes.Any),
            new("z", ApprovalNodeKinds.End)
        ],
        [
            E1("e1", "s", "f1"), E1("e2", "f1", "f2"), E1("e3", "f1", "n1"), E1("e4", "n1", "f2"), E1("e5", "f2", "n2"),
            E1("e6", "f2", "a"), E1("e7", "f2", "n4"), E1("e8", "n2", "j1"), E1("e9", "n4", "j1"), E1("e10", "a", "j2"),
            E1("e11", "j1", "j2"), E1("e12", "j2", "z")
        ]);
        var errors = await Validate(d);
        E(errors, "nodes.f1").Should().Be(ApprovalMessages.Graph("branchesMerge"));
        E(errors, "edges.e11").Should().Be(ApprovalMessages.Graph("edgeIntoRegion"));
        E(errors, "nodes.z").Should().Be(ApprovalMessages.Graph("endInRegion"));
    }

    [Fact]
    public async Task Graph_NoApprovalNode_Rejected_U5()
    {
        var errors = await Validate(new ApprovalDefinition(ApprovalShapes.Graph, null, null, null, [],
            [new("start", ApprovalNodeKinds.Start), new("end", ApprovalNodeKinds.End)], [new("e1", "start", "end")]));
        E(errors, "graph").Should().Be(ApprovalMessages.Graph("approvalNodesNone"));
    }

    [Fact]
    public async Task Graph_MaxBranchesFromModulePolicy()
    {
        var d = await DesignAsync();
        d = d with
        {
            Nodes = [.. d.Nodes!, Approval("ops", Group(BoardGroup), "營運")],
            Edges = [.. d.Edges!, new("e11", "split", "ops"), new("e12", "ops", "merge")]
        };
        (await Validate(d)).Keys.Should().Equal("shape");   // 三條分支，出廠上限 5

        Db.Context.Add(new ModulePolicy { ModuleId = _r.ModuleId, Code = PolicyCatalog.ApprovalMaxBranches.Code, Value = "2", ModifiedUserId = "admin" });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        E(await Validate(d), "nodes.split").Should().Be(ApprovalMessages.Graph("branchesTooMany", 10, 2));
    }

    [Fact]
    public async Task Graph_PreviousSequence_KeepOldApplies()
    {
        _r = await BuildPurchase();
        ApprovalDefinition Linear(int n) => new(ApprovalShapes.Graph, null, null, null, [],
            [new("start", ApprovalNodeKinds.Start), .. Enumerable.Range(1, n).Select(i => Approval($"s{i}", Manager(1), $"s{i}")), new("end", ApprovalNodeKinds.End)],
            [new("e0", "start", "s1"), .. Enumerable.Range(1, n - 1).Select(i => new ApprovalEdgeDef($"e{i}", $"s{i}", $"s{i + 1}")), new($"e{n}", $"s{n}", "end")]);
        var previous = Def(null, Enumerable.Range(1, 12).Select(i => Stage($"s{i}", Manager(1))).ToArray());   // 政策上限 10，舊版 12 關

        E(await Validate(Linear(13)), "graph").Should().Be(ApprovalMessages.Graph("approvalNodesTooMany", 10, 5));
        E(await Validate(Linear(13), previous), "graph").Should().Be(ApprovalMessages.Graph("approvalNodesTooMany", 10, 5));
        (await Validate(Linear(12), previous)).Keys.Should().Equal("shape");
        E(await Validate(Linear(12)), "graph").Should().Be(ApprovalMessages.Graph("approvalNodesTooMany", 10, 5));
    }

    [Fact]
    public async Task Graph_PreviousSequence_NodeAttributesCompareWithSameCode()
    {
        // 舊版 sequence 的關卡 mgr 逾期 9999 小時（超過政策），同代號的簽核節點沿用不擋；換代號就擋
        var d = await DesignAsync();
        var previous = Def(null, Stage("mgr", Manager(1), dueHours: 9999));
        (await Validate(Mod(d, "mgr", n => n with { DueHours = 9999 }), previous)).Keys.Should().NotContain("nodes.mgr.dueHours");
        E(await Validate(Mod(d, "mgr", n => n with { DueHours = 9999 }), Def(null, Stage("other", Manager(1), dueHours: 9999))),
            "nodes.mgr.dueHours").Should().Contain("1 到");
    }

    [Fact]
    public void GraphMessages_CoverEveryCode()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "poc", "shared", "approval-graph-cases.json"))) dir = dir.Parent;
        var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "poc", "shared", "approval-graph-cases.json"))).RootElement;
        // 案例檔沒覆蓋到的代碼（程式防線或案例太大）一併列出
        var codes = doc.GetProperty("cases").EnumerateArray()
            .SelectMany(c => c.GetProperty("errors").EnumerateArray().Concat(c.GetProperty("warnings").EnumerateArray()))
            .Select(t => t[2].GetString()!).Concat(["edgeOutOfRegion", "joinForkInvalid", "nodesTooMany", "edgesTooMany", "nestingTooDeep"]).Distinct().ToList();
        codes.Should().HaveCountGreaterThan(30);
        foreach (var code in codes)
            ApprovalMessages.Graph(code).Should().NotContain("流程圖結構不正確", $"代碼 {code} 要有訊息對照");
    }
}
