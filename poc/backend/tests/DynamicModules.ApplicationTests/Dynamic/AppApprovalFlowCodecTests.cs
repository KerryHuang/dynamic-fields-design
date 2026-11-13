using System.Text.Json;
using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.ApplicationTests.Dynamic.Approvals;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>簽核流程的代號轉換（開啟方向）與略過群組的頂替清單（簽核設計 §14.4、§17.5 審查第 1 項）。純函式。</summary>
public class AppApprovalFlowCodecTests
{
    private static I18nText T(string zh) => new([KeyValuePair.Create("zh-TW", zh)]);

    private static readonly List<AppGroup> Groups =
        [new("purchaseLead", T("採購主管")), new("board", T("董事會")), new("buyers", T("採購人員"))];

    private static readonly Dictionary<string, long> AllGroups = new() { ["purchaseLead"] = 11, ["board"] = 12, ["buyers"] = 13 };

    private static readonly AppReportCodec.Lookups Lookups = new(
        code => code == "po" ? "t8-po" : null,
        name => name switch { "po.total" => 101, "po.memo" => 102, "po.buyer" => 103, "po.status" => 104, _ => null },
        name => name switch { "po.draft" => 201, "po.confirmed" => 202, _ => null },
        name => name switch { "po.total" => FieldType.Decimal, "po.memo" => FieldType.Text, "po.buyer" => FieldType.User, "po.status" => FieldType.Enum, _ => null });

    private static string GroupName(string code) => Groups.Single(g => g.Code == code).Name.Resolve("zh-TW");

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static AppApprovalStage Stage(string code, AppApproverSource approvers, string mode = "any", string? onEmpty = "fallback",
        List<string>? editable = null, int? dueHours = null, JsonElement? when = null) =>
        new(code, T(code), when, approvers, mode, editable, dueHours, onEmpty);

    private static AppApproverSource Group(params string[] codes) => new("group", [.. codes], null, null, null, null, null);

    private static AppApprovalFlow Flow(AppApproverSource? fallback = null, params AppApprovalStage[] stages) =>
        new("t8-po-large", T("採購單（大額）"), true, "po", "confirmed", 1, "sequence",
            Json("""{ "all": [ { "field": "po.total", "op": "gte", "value": 100000 } ] }"""), "draft", fallback, [.. stages]);

    private static Dictionary<string, long> Without(params string[] skipped) =>
        AllGroups.Where(kv => !skipped.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

    [Fact]
    public void ToInput_MapsCodesToIds()
    {
        var f = Flow(Group("purchaseLead"),
            Stage("lead", Group("purchaseLead"), editable: ["po.memo"], dueHours: 24,
                when: Json("""{ "all": [ { "field": "po.status", "op": "eq", "value": "opt_draft" } ] }""")),
            Stage("board", Group("board", "buyers"), mode: "all", onEmpty: null));
        var (input, errors) = AppApprovalFlowCodec.ToInput(f, Lookups, AllGroups, GroupName);
        errors.Should().BeEmpty();
        input!.Key.Should().Be("t8-po-large");
        input.Name.Resolve("zh-TW").Should().Be("採購單（大額）");
        input.ToOptionId.Should().Be(202);
        input.Priority.Should().Be(1);
        input.Active.Should().BeTrue();
        var d = input.Definition;
        d.Shape.Should().Be("sequence");
        d.RejectTo.Should().Be(201);
        d.When!.Value.GetRawText().Should().Contain("101");
        d.FallbackApprovers!.GroupIds.Should().Equal(11);
        d.Stages[0].EditableFields.Should().Equal(102);
        d.Stages[0].DueHours.Should().Be(24);
        d.Stages[0].When!.Value.GetRawText().Should().Contain("104").And.Contain("opt_201");
        d.Stages[1].Approvers.GroupIds.Should().Equal(12, 13);
        d.Stages[1].Mode.Should().Be("all");
        d.Stages[1].OnEmpty.Should().Be("fallback", "省略時是 fallback");
        d.Stages[1].EditableFields.Should().BeEmpty();
    }

    [Fact]
    public void ToInput_SkippedApproverGroup_BecomesUnmatchedUnit_KeepsOthers()
    {
        var f = Flow(null, Stage("board", Group("board", "purchaseLead"), mode: "all"));
        var (input, _) = AppApprovalFlowCodec.ToInput(f, Lookups, Without("board"), GroupName);
        var source = input!.Definition.Stages[0].Approvers;
        source.GroupIds.Should().Equal(11);
        source.UnmatchedGroups.Should().Equal("董事會");
    }

    [Fact]
    public void ToInput_SkippedGroup_OnEmptySkip_TurnsFallback()
    {
        var f = Flow(null, Stage("board", Group("board", "buyers"), onEmpty: "skip"), Stage("lead", Group("purchaseLead"), onEmpty: "skip"));
        var (input, _) = AppApprovalFlowCodec.ToInput(f, Lookups, Without("board"), GroupName);
        input!.Definition.Stages[0].OnEmpty.Should().Be("fallback");
        input.Definition.Stages[1].OnEmpty.Should().Be("skip", "沒有略過群組的關卡照套件");
    }

    [Fact]
    public void ToInput_SkippedCandidateGroup_SetsCandidatesUnmatched()
    {
        var userField = new AppApproverSource("userField", null, null, "po.buyer", ["buyers", "board"], null, null);
        var pick = new AppApproverSource("initiatorPick", null, null, null, ["board"], 1, 2);
        var (input, _) = AppApprovalFlowCodec.ToInput(Flow(null, Stage("owner", userField), Stage("pick", pick)), Lookups, Without("board"), GroupName);
        var stages = input!.Definition.Stages;
        stages[0].Approvers.FieldId.Should().Be(103);
        stages[0].Approvers.CandidatesUnmatched.Should().BeTrue();
        stages[0].Approvers.CandidateGroupIds.Should().Equal(13);
        stages[1].Approvers.CandidatesUnmatched.Should().BeTrue();
        stages[1].Approvers.CandidateGroupIds.Should().BeEmpty();
        stages[1].Approvers.Min.Should().Be(1);
        stages[1].Approvers.Max.Should().Be(2);
    }

    [Fact]
    public void ToInput_SkippedFallbackGroup_UsesPolicyDefault()
    {
        var (input, _) = AppApprovalFlowCodec.ToInput(Flow(Group("board", "purchaseLead"), Stage("lead", Group("purchaseLead"))),
            Lookups, Without("board"), GroupName);
        input!.Definition.FallbackApprovers.Should().BeNull();
    }

    [Fact]
    public void ToInput_NothingSkipped_NoUnmatched()
    {
        var f = Flow(new AppApproverSource("manager", null, 2, null, null, null, null),
            Stage("lead", Group("purchaseLead")), Stage("owner", new AppApproverSource("userField", null, null, "po.buyer", ["buyers"], null, null)));
        var (input, _) = AppApprovalFlowCodec.ToInput(f, Lookups, AllGroups, GroupName);
        input!.Definition.Stages.Should().AllSatisfy(s =>
        {
            s.Approvers.UnmatchedGroups.Should().BeNull();
            s.Approvers.CandidatesUnmatched.Should().BeFalse();
        });
        input.Definition.FallbackApprovers!.Level.Should().Be(2);
    }

    [Fact]
    public void ToInput_UnresolvedField_ReturnsError()
    {
        var (input, errors) = AppApprovalFlowCodec.ToInput(Flow(null, Stage("lead", Group("purchaseLead"), editable: ["po.gone"])),
            Lookups, AllGroups, GroupName);
        input.Should().BeNull();
        errors.Should().Contain("引用了套件沒有的欄位 po.gone");
    }

    [Fact]
    public void Substitutions_ListEachReplacedUnit()
    {
        var userField = new AppApproverSource("userField", null, null, "po.buyer", ["board"], null, null);
        var withFallback = Flow(Group("purchaseLead"), Stage("board", Group("board", "buyers"), mode: "all"), Stage("owner", userField));
        var mapped = new HashSet<string> { "purchaseLead", "buyers" };
        AppApprovalFlowCodec.Substitutions(withFallback, mapped, Groups).Select(s => (s.Stage, s.Group, s.Kind, s.ReplacedBy))
            .Should().Equal(("board", "board", "approvers", "flowFallback"), ("owner", "board", "candidates", "flowFallback"));

        // 流程沒有備援，或備援的群組本身被略過：頂替的是政策預設備援
        var noFallback = Flow(null, Stage("board", Group("board", "buyers")));
        AppApprovalFlowCodec.Substitutions(noFallback, mapped, Groups).Single().ReplacedBy.Should().Be("policyDefault");
        var skippedFallback = Flow(Group("board"), Stage("lead", Group("buyers")));
        var s = AppApprovalFlowCodec.Substitutions(skippedFallback, mapped, Groups).Single();
        (s.Stage, s.StageName, s.Group, s.Kind, s.ReplacedBy).Should().Be(((string?)null, (I18nText?)null, "board", "fallback", "policyDefault"));
        s.GroupName.Resolve("zh-TW").Should().Be("董事會");
        s.FlowName.Resolve("zh-TW").Should().Be("採購單（大額）");

        AppApprovalFlowCodec.Substitutions(withFallback, AllGroups.Keys.ToHashSet(), Groups).Should().BeEmpty();
    }
}

/// <summary>轉換結果要通過設計器的定義驗證（在資料庫建好的採購單模組上）。</summary>
public class AppApprovalFlowCodecDefinitionTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    [Fact]
    public async Task ToInput_ResultPassesDefinitionValidator()
    {
        var r = await BuildPurchase();
        var groups = new Dictionary<string, long> { ["purchaseLead"] = PurchaseLeadGroup, ["finance"] = FinanceGroup };
        var lookups = new AppReportCodec.Lookups(
            code => code == "po" ? r.Module : null,
            name => name switch { "po.total" => r.Total, "po.memo" => r.Memo, "po.owner" => r.Owner, _ => null },
            name => name switch { "po.draft" => r.Draft, "po.confirmed" => r.Confirmed, _ => null },
            name => name switch { "po.total" => FieldType.Formula, "po.memo" => FieldType.Text, "po.owner" => FieldType.User, _ => null });
        var name = new I18nText([KeyValuePair.Create("zh-TW", "採購單（大額）")]);
        var f = new AppApprovalFlow("t8-po-large", name, true, "po", "confirmed", 1, "sequence",
            JsonDocument.Parse("""{ "all": [ { "field": "po.total", "op": "gte", "value": 100000 } ] }""").RootElement.Clone(), "draft",
            new AppApproverSource("group", ["purchaseLead"], null, null, null, null, null),
            [
                new AppApprovalStage("lead", name, null, new AppApproverSource("manager", null, 1, null, null, null, null), "any", ["po.memo"], 24, null),
                new AppApprovalStage("board", name, null, new AppApproverSource("group", ["board", "finance"], null, null, null, null, null), "all", null, 48, "skip"),
                new AppApprovalStage("owner", name, null, new AppApproverSource("userField", null, null, "po.owner", ["purchaseLead"], null, null), "any", null, null, null)
            ]);
        var (input, errors) = AppApprovalFlowCodec.ToInput(f, lookups, groups, code => code == "board" ? "董事會" : code);
        errors.Should().BeEmpty();
        input!.Definition.Stages[1].Approvers.UnmatchedGroups.Should().Equal("董事會");

        var module = (await Svc<IModuleRepository>().GetByKeyAsync(r.Module, includeChildren: true, CancellationToken.None))!;
        (await Svc<ApprovalDefinitionValidator>().ValidateAsync(module, module.Workflow!, input.Definition, null, CancellationToken.None))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task ToInput_AllApproverGroupsSkipped_PassesDefinitionValidator()
    {
        // 審查 M1：一關的群組（或 userField 的候選群組）全部被略過，流程仍要存得進去，該關整個由備援頂替（§14.4 流程一律保留）
        var r = await BuildPurchase();
        var lookups = new AppReportCodec.Lookups(
            code => code == "po" ? r.Module : null,
            name => name == "po.owner" ? r.Owner : null,
            name => name switch { "po.draft" => r.Draft, "po.confirmed" => r.Confirmed, _ => null },
            name => name == "po.owner" ? FieldType.User : null);
        var name = new I18nText([KeyValuePair.Create("zh-TW", "採購單（大額）")]);
        var f = new AppApprovalFlow("t8-po-large", name, true, "po", "confirmed", 1, "sequence", null, null, null,
            [
                new AppApprovalStage("board", name, null, new AppApproverSource("group", ["board"], null, null, null, null, null), "all", null, null, "skip"),
                new AppApprovalStage("owner", name, null, new AppApproverSource("userField", null, null, "po.owner", ["board"], null, null), "any", null, null, null)
            ]);
        var (input, errors) = AppApprovalFlowCodec.ToInput(f, lookups, new Dictionary<string, long>(), code => code == "board" ? "董事會" : code);
        errors.Should().BeEmpty();
        var stages = input!.Definition.Stages;
        stages[0].Approvers.GroupIds.Should().BeEmpty();
        stages[0].Approvers.UnmatchedGroups.Should().Equal("董事會");
        stages[0].OnEmpty.Should().Be("fallback");
        stages[1].Approvers.CandidateGroupIds.Should().BeEmpty();
        stages[1].Approvers.CandidatesUnmatched.Should().BeTrue();

        var module = (await Svc<IModuleRepository>().GetByKeyAsync(r.Module, includeChildren: true, CancellationToken.None))!;
        (await Svc<ApprovalDefinitionValidator>().ValidateAsync(module, module.Workflow!, input.Definition, null, CancellationToken.None))
            .Should().BeEmpty();
    }
}
