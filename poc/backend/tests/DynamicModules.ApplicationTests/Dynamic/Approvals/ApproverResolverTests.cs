using System.Text.Json;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Organization;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>簽核人解析的純函式（設計 §6.1、§6.4、§6.5）；組織與群組自己組。</summary>
public class ApproverResolverTests
{
    private const long Finance = 1, Board = 2, PurchaseLead = 3, Big = 4;
    private static I18nText T(string zh) => new([KeyValuePair.Create("zh-TW", zh)]);
    private static readonly I18nText Name = new([KeyValuePair.Create("zh-TW", "關卡")]);

    private static readonly Field OwnerField = new() { Id = 50, Key = "f_50", Type = FieldType.User, LabelI18n = new([KeyValuePair.Create("zh-TW", "負責人")]) };

    private static ResolveContext Ctx(string initiator, IReadOnlyList<string>? picks = null, string[]? admins = null, string[]? inactive = null,
        Dictionary<string, JsonElement?>? values = null)
    {
        string[] users = ["gm", "mgrA", "emp1", "lead1", "emp3", "buyer1", "buyer2", "fin1", "fin2", "admin", .. Enumerable.Range(1, 25).Select(i => $"u{i}")];
        var org = new OrgSnapshot(
            new Dictionary<long, OrgDepartment>
            {
                [10] = new(10, null, "gm", true), [11] = new(11, 10, "mgrA", true), [12] = new(12, 11, "lead1", true),
                [13] = new(13, 10, null, true)
            },
            new Dictionary<string, long> { ["gm"] = 10, ["mgrA"] = 11, ["lead1"] = 12, ["emp3"] = 12, ["buyer1"] = 13, ["buyer2"] = 13 },
            users.Except(inactive ?? []).ToHashSet());
        var groups = new Dictionary<long, IReadOnlyList<string>>
        {
            [Finance] = ["fin1", "fin2"], [Board] = [], [PurchaseLead] = ["buyer2"], [Big] = Enumerable.Range(1, 21).Select(i => $"u{i}").ToList()
        };
        var module = new Module { Blocks = [new Block { Fields = [OwnerField] }] };
        return new ResolveContext(module, initiator, values ?? [], picks, org, groups, admins ?? ["admin"], 20,
            new Dictionary<long, I18nText> { [Finance] = T("finance"), [Board] = T("board"), [PurchaseLead] = T("purchase-lead"), [Big] = T("big") });
    }

    private static ApproverSource Group(params long[] ids) => new(ApprovalSources.Group, GroupIds: [.. ids]);

    private static StageResolution Resolve(ResolveContext ctx, ApproverSource source, string mode = ApprovalModes.Any,
        string onEmpty = ApprovalOnEmpty.Fallback, ApproverSource? fallback = null) =>
        ApproverResolver.Resolve(ctx, Name, mode, source, fallback, onEmpty);

    [Fact]
    public void Group_RemovesInitiator_Dedups()
    {
        var r = Resolve(Ctx("fin1"), Group(Finance, Finance));
        r.Error.Should().BeNull();
        r.Seats.Should().ContainSingle().Which.UserIds.Should().Equal("fin2");
    }

    [Fact]
    public void All_EmptyGroupUnit_GetsFallbackSeat()
    {
        var r = Resolve(Ctx("buyer1"), Group(Board, PurchaseLead), ApprovalModes.All);
        r.Error.Should().BeNull();
        r.Seats.Select(s => s.SeatKey).Should().Equal("u:buyer2", "fb:0");
        r.Seats[1].UserIds.Should().Equal("admin");
        r.Seats[1].Reason.Should().Be(ApprovalTaskReason.Fallback);
        r.Seats[1].FallbackFor.Should().Be("board");
    }

    [Fact]
    public void Any_EmptyUnit_AddsFallbackToCandidates()
    {
        var r = Resolve(Ctx("buyer1"), Group(Board, PurchaseLead));
        r.Seats.Should().OnlyContain(s => s.SeatKey == ApproverResolver.AnySeat);
        r.Seats.SelectMany(s => s.UserIds).Should().BeEquivalentTo(["buyer2", "admin"]);
        r.Seats.Single(s => s.Reason == ApprovalTaskReason.Fallback).UserIds.Should().Equal("admin");
    }

    [Fact]
    public void Skip_OnlyWhenAllUnitsEmpty()
    {
        Resolve(Ctx("buyer1"), Group(Board), onEmpty: ApprovalOnEmpty.Skip).AllUnitsEmpty.Should().BeTrue();
        var partial = Resolve(Ctx("buyer1"), Group(Board, PurchaseLead), ApprovalModes.All, ApprovalOnEmpty.Skip);
        partial.AllUnitsEmpty.Should().BeFalse();
        partial.Seats.Select(s => s.SeatKey).Should().Equal("u:buyer2", "fb:0");
    }

    [Fact]
    public void FallbackOnlyInitiator_Error()
    {
        var r = Resolve(Ctx("admin"), Group(Board));
        r.Error!.Resolve(null).Should().Be(ApprovalMessages.NoApprover("關卡", "board").Resolve(null));
        r.Seats.Should().BeEmpty();
    }

    [Fact]
    public void Over20_Error() =>
        Resolve(Ctx("buyer1"), Group(Big)).Error!.Resolve(null).Should().Be(ApprovalMessages.TooManyApprovers("關卡", 20).Resolve(null));

    [Fact]
    public void InactiveUser_Excluded()
    {
        var r = Resolve(Ctx("buyer1", inactive: ["fin2"]), Group(Finance));
        r.Seats.Should().ContainSingle().Which.UserIds.Should().Equal("fin1");
        // users 來源：停用的那一位是空的單位 → 由備援頂替
        var users = Resolve(Ctx("buyer1", inactive: ["fin2"]), new ApproverSource(ApprovalSources.Users, UserIds: ["fin1", "fin2"]), ApprovalModes.All);
        users.Seats.Select(s => s.SeatKey).Should().Equal("u:fin1", "fb:0");
        users.Seats[1].FallbackFor.Should().Be("fin2");
    }

    [Fact]
    public void UnmatchedGroups_ActAsEmptyUnits()
    {
        var r = Resolve(Ctx("buyer1"), new ApproverSource(ApprovalSources.Group, GroupIds: [PurchaseLead], UnmatchedGroups: ["board"]), ApprovalModes.All);
        r.Seats.Select(s => s.SeatKey).Should().Equal("u:buyer2", "fb:0");
        r.Seats[1].FallbackFor.Should().Be("board");
    }

    [Fact]
    public void Manager_VacantGoesUp_InitiatorIsManagerGoesUp_NoDepartmentFallback()
    {
        Resolve(Ctx("buyer1"), new ApproverSource(ApprovalSources.Manager, Level: 1)).Seats.Single().UserIds.Should().Equal("gm");
        Resolve(Ctx("lead1"), new ApproverSource(ApprovalSources.Manager, Level: 1)).Seats.Single().UserIds.Should().Equal("mgrA");
        Resolve(Ctx("emp3"), new ApproverSource(ApprovalSources.Manager, Level: 2)).Seats.Single().UserIds.Should().Equal("mgrA");
        var none = Resolve(Ctx("fin1"), new ApproverSource(ApprovalSources.Manager, Level: 1));
        none.Seats.Single().Reason.Should().Be(ApprovalTaskReason.Fallback);
        none.Seats.Single().FallbackFor.Should().Be(ApprovalMessages.ManagerLabel(1));
    }

    [Fact]
    public void Fallback_ManagerSource_ResolvesInitiatorsManager()
    {
        var r = Resolve(Ctx("emp3"), Group(Board), fallback: new ApproverSource(ApprovalSources.Manager, Level: 1));
        r.Seats.Single().UserIds.Should().Equal("lead1");
    }

    [Fact]
    public void UserField_NotCandidateOrSelf_IsEmpty()
    {
        var src = new ApproverSource(ApprovalSources.UserField, FieldId: OwnerField.Id, CandidateGroupIds: [Finance]);
        JsonElement? V(string u) => JsonSerializer.SerializeToElement(u);
        Resolve(Ctx("emp3", values: new() { ["f_50"] = V("fin1") }), src).Seats.Single().UserIds.Should().Equal("fin1");
        Resolve(Ctx("emp3", values: new() { ["f_50"] = V("gm") }), src).Seats.Single().Reason.Should().Be(ApprovalTaskReason.Fallback);
        Resolve(Ctx("fin1", values: new() { ["f_50"] = V("fin1") }), src).Seats.Single().Reason.Should().Be(ApprovalTaskReason.Fallback);
    }

    [Fact]
    public void InitiatorPick_EachPickIsAUnit()
    {
        var src = new ApproverSource(ApprovalSources.InitiatorPick, Min: 1, Max: 3);
        var r = Resolve(Ctx("emp3", picks: ["fin1", "fin2"]), src, ApprovalModes.All);
        r.Seats.Select(s => s.SeatKey).Should().Equal("u:fin1", "u:fin2");
    }
    [Fact]
    public void All_TwoEmptyUnits_FallbackPoolSmallerThanSeats_Error()
    {
        // 兩個空單位、備援只有一人：一人一票下第二席永遠補不上，啟動時就要失敗（設計 §6.5）
        var src = new ApproverSource(ApprovalSources.Users, UserIds: ["u1", "u2"]);
        var one = Resolve(Ctx("buyer1", inactive: ["u1", "u2"]), src, ApprovalModes.All);
        one.Error!.Resolve(null).Should().Be(ApprovalMessages.NoApprover("關卡", "u2").Resolve(null));
        one.Seats.Should().BeEmpty();

        var two = Resolve(Ctx("buyer1", inactive: ["u1", "u2"], admins: ["admin", "gm"]), src, ApprovalModes.All);
        two.Error.Should().BeNull();
        two.Seats.Select(s => s.SeatKey).Should().Equal("fb:0", "fb:1");
    }

    [Fact]
    public void Any_FallbackAllAlreadyCandidates_Error()
    {
        // 或簽：備援移除已有任務的人後為空 → 409（設計 §6.5 字面；審查建議 7）
        var r = Resolve(Ctx("buyer1"), Group(Board, PurchaseLead), fallback: new ApproverSource(ApprovalSources.Users, UserIds: ["buyer2"]));
        r.Error!.Resolve(null).Should().Be(ApprovalMessages.NoApprover("關卡", "board").Resolve(null));
        r.Seats.Should().BeEmpty();
    }

    [Fact]
    public void All_PickCandidatesUnmatched_FallbackSeatsKeepMin()
    {
        // 審查 M1：自選候選群組對不上時，會簽仍要湊滿 min 票（設計 §14.4），不能降成一個備援席位
        var src = new ApproverSource(ApprovalSources.InitiatorPick, Min: 2, Max: 2, CandidatesUnmatched: true);
        var two = Resolve(Ctx("buyer1"), src, ApprovalModes.All, fallback: new ApproverSource(ApprovalSources.Users, UserIds: ["gm", "mgrA"]));
        two.Error.Should().BeNull();
        two.Seats.Select(s => s.SeatKey).Should().Equal("fb:0", "fb:1");

        var one = Resolve(Ctx("buyer1"), src, ApprovalModes.All, fallback: new ApproverSource(ApprovalSources.Users, UserIds: ["gm"]));
        one.Error!.Resolve(null).Should().Be(ApprovalMessages.NoApprover("關卡", ApprovalMessages.PickLabel).Resolve(null));
        one.Seats.Should().BeEmpty();
    }

    [Fact]
    public void Skip_SourceUnmatched_GoesToFallback_NotSkipped()
    {
        // 審查 M2：來源對不上是設定缺漏、不是「解析為空」（設計 §6.5、§14.4），onEmpty = skip 也要交給備援
        JsonElement? V(string u) => JsonSerializer.SerializeToElement(u);
        var sources = new[]
        {
            new ApproverSource(ApprovalSources.Group, GroupIds: [Board], UnmatchedGroups: ["gone"]),
            new ApproverSource(ApprovalSources.InitiatorPick, Min: 1, Max: 1, CandidatesUnmatched: true),
            new ApproverSource(ApprovalSources.UserField, FieldId: OwnerField.Id, CandidateGroupIds: [Finance], CandidatesUnmatched: true)
        };
        foreach (var src in sources)
        {
            var r = Resolve(Ctx("buyer1", values: new() { ["f_50"] = V("fin1") }), src, onEmpty: ApprovalOnEmpty.Skip);
            r.AllUnitsEmpty.Should().BeFalse(src.Source);
            r.Error.Should().BeNull(src.Source);
            r.Seats.Should().ContainSingle().Which.Should().Match<SeatPlan>(s => s.Reason == ApprovalTaskReason.Fallback && s.UserIds.SequenceEqual(new[] { "admin" }));

            // 沒有備援（備援只有發起人自己）→ 409，不跳關
            var none = Resolve(Ctx("buyer1", values: new() { ["f_50"] = V("fin1") }), src, onEmpty: ApprovalOnEmpty.Skip,
                fallback: new ApproverSource(ApprovalSources.Users, UserIds: ["buyer1"]));
            none.AllUnitsEmpty.Should().BeFalse(src.Source);
            none.Error.Should().NotBeNull(src.Source);
        }
        // 對照：真的解析為空（board 沒有成員）才跳過
        Resolve(Ctx("buyer1"), Group(Board), onEmpty: ApprovalOnEmpty.Skip).AllUnitsEmpty.Should().BeTrue();
    }

    [Theory]
    [InlineData(ApprovalOnEmpty.Fallback)]
    [InlineData(ApprovalOnEmpty.Skip)]
    public void NoUnits_ZeroSeats_Error(string onEmpty)
    {
        // 單位清單是空的、或來源不認得：不能建出沒有任務的關卡，也不能當成「全部單位都空」而跳過（審查建議 9）
        var empty = Resolve(Ctx("buyer1"), new ApproverSource(ApprovalSources.Users, UserIds: []), ApprovalModes.All, onEmpty);
        empty.Error!.Resolve(null).Should().Be(ApprovalMessages.NoApprover("關卡", ApprovalSources.Users).Resolve(null));
        empty.AllUnitsEmpty.Should().BeFalse();
        var unknown = Resolve(Ctx("buyer1"), new ApproverSource("bogus"), onEmpty: onEmpty);
        unknown.Error!.Resolve(null).Should().Be(ApprovalMessages.NoApprover("關卡", "bogus").Resolve(null));
        unknown.Seats.Should().BeEmpty();
    }
}
