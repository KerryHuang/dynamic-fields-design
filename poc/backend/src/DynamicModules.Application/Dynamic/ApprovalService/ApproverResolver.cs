using System.Text.Json;
using DynamicModules.Application.Dynamic.OrganizationService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Organization;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>一個席位：同席位任一人同意即滿足。Reason 是 resolved 或 fallback；FallbackFor 是被頂替的來源說明。</summary>
public sealed record SeatPlan(string SeatKey, IReadOnlyList<string> UserIds, string Reason, string? FallbackFor);

/// <summary>一關的解析結果；Error 非 null 代表這次請求要回 409（找不到人、超過 20 人）。</summary>
public sealed record StageResolution(IReadOnlyList<SeatPlan> Seats, bool AllUnitsEmpty, string? Error);

/// <summary>解析的輸入：Picks 是這一關的自選結果；GroupNames 給錯誤與歷程說明用。</summary>
public sealed record ResolveContext(
    Module Module, string InitiatorUserId, IReadOnlyDictionary<string, JsonElement?> Values, IReadOnlyList<string>? Picks, OrgSnapshot Org,
    IReadOnlyDictionary<long, IReadOnlyList<string>> GroupMembers, IReadOnlyList<string> ActiveAdmins,
    IReadOnlyDictionary<long, string>? GroupNames = null);

/// <summary>
/// 簽核人解析（設計 §6.1、§6.4、§6.5）：先依來源切出來源單位，每個單位只留啟用中、不是發起人的人；
/// 空的單位由備援簽核人頂替（會簽另佔一席、或簽併入候選），onEmpty = skip 只在全部單位都空時跳過。
/// </summary>
public sealed class ApproverResolver(OrgSnapshotLoader org, IPermissionRepository permissions)
{
    public const string AnySeat = "any";

    public async Task<ResolveContext> ContextAsync(
        Module module, string initiator, IReadOnlyDictionary<string, JsonElement?> values, IReadOnlyList<string>? picks, CancellationToken ct)
    {
        var snapshot = await org.LoadAsync(ct);
        var members = (await permissions.ListMembershipsAsync(ct)).GroupBy(m => m.GroupId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(m => m.UserId).ToList());
        var groups = (await permissions.ListGroupsAsync(ct)).ToDictionary(g => g.Id, g => g.NameI18n.Resolve("zh-TW"));
        var admins = (await permissions.ListUsersAsync(ct)).Where(u => u.IsActive && u.IsSystemAdmin).Select(u => u.UserId).OrderBy(u => u).ToList();
        return new ResolveContext(module, initiator, values, picks, snapshot, members, admins, groups);
    }

    public static StageResolution Resolve(ResolveContext ctx, I18nText stageName, string mode, ApproverSource source, ApproverSource? fallback, string onEmpty)
    {
        var stage = stageName.Resolve("zh-TW");
        var units = Units(ctx, source).Select(u => (u.Label, Users: Clean(ctx, u.Users))).ToList();
        // 一個單位都沒有（清單空、來源不認得）：不能建出沒有任務的關卡，也不算「全部單位都空」而跳過（fail closed）
        if (units.Count == 0) return new([], false, ApprovalMessages.NoApprover(stage, source.Source));
        if (units.All(u => u.Users.Count == 0) && onEmpty == ApprovalOnEmpty.Skip) return new([], true, null);

        var empty = units.Where(u => u.Users.Count == 0).Select(u => u.Label).ToList();
        var resolved = units.SelectMany(u => u.Users).Distinct(StringComparer.Ordinal).ToList();
        var backups = empty.Count == 0 ? [] : FallbackUsers(ctx, fallback);
        var seats = new List<SeatPlan>();

        if (mode == ApprovalModes.All)
        {
            seats.AddRange(resolved.Select(u => new SeatPlan($"u:{u}", [u], ApprovalTaskReason.Resolved, null)));
            var pool = backups.Where(u => !resolved.Contains(u)).ToList();
            // 備援席位共用同一份名單、會簽一人一票：人數少於空單位數，後面的席位永遠補不上（設計 §6.5）
            if (pool.Count < empty.Count) return new([], false, ApprovalMessages.NoApprover(stage, empty[pool.Count]));
            for (var i = 0; i < empty.Count; i++)
            {
                seats.Add(new SeatPlan($"fb:{i}", pool, ApprovalTaskReason.Fallback, empty[i]));
            }
        }
        else
        {
            if (resolved.Count > 0) seats.Add(new SeatPlan(AnySeat, resolved, ApprovalTaskReason.Resolved, null));
            if (empty.Count > 0)
            {
                var extra = backups.Where(u => !resolved.Contains(u)).ToList();
                // 移除已有任務的人後備援為空 → 409（設計 §6.5），即使這關還有其他候選人
                if (extra.Count == 0) return new([], false, ApprovalMessages.NoApprover(stage, empty[0]));
                seats.Add(new SeatPlan(AnySeat, extra, ApprovalTaskReason.Fallback, string.Join("、", empty.Distinct())));
            }
        }

        if (seats.Count == 0) return new([], false, ApprovalMessages.NoApprover(stage, empty.FirstOrDefault() ?? source.Source));
        if (seats.SelectMany(s => s.UserIds).Distinct(StringComparer.Ordinal).Count() > ApprovalLimits.MaxApprovers)
            return new([], false, ApprovalMessages.TooManyApprovers(stage));
        return new(seats, false, null);
    }

    /// <summary>來源單位（設計 §6.5 表）：群組各一、使用者各一、manager／userField 整個一個、自選的人各一、略過的群組各一（為空）。</summary>
    private static IEnumerable<(string Label, IReadOnlyList<string> Users)> Units(ResolveContext ctx, ApproverSource source)
    {
        foreach (var name in source.UnmatchedGroups ?? []) yield return (name, []);
        switch (source.Source)
        {
            case ApprovalSources.Group:
                foreach (var id in source.GroupIds ?? [])
                    yield return (GroupLabel(ctx, id), ctx.GroupMembers.GetValueOrDefault(id) ?? []);
                break;
            case ApprovalSources.Users:
                foreach (var id in source.UserIds ?? []) yield return (id, [id]);
                break;
            case ApprovalSources.Manager:
                var level = source.Level ?? 1;
                var manager = ManagerChain.Resolve(ctx.Org, ctx.InitiatorUserId, level);
                yield return (ApprovalMessages.ManagerLabel(level), manager is null ? [] : [manager]);
                break;
            case ApprovalSources.UserField:
                var field = ctx.Module.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == source.FieldId);
                var label = field?.LabelI18n.Resolve("zh-TW") ?? $"#{source.FieldId}";
                var value = field is not null && ctx.Values.TryGetValue(field.Key, out var v) && v is { ValueKind: JsonValueKind.String } s
                    ? s.GetString() : null;
                var candidates = Candidates(ctx, source);
                yield return (label, value is not null && !source.CandidatesUnmatched && (candidates is null || candidates.Contains(value)) ? [value] : []);
                break;
            case ApprovalSources.InitiatorPick:
                if (source.CandidatesUnmatched)
                {
                    // 對不上時由備援頂替，會簽仍要湊滿原本的票數（設計 §14.4，審查 M1）：依 min 產生同樣數量的空單位
                    for (var i = 0; i < Math.Max(1, source.Min ?? 1); i++) yield return (ApprovalMessages.PickLabel, []);
                    break;
                }
                var allowed = Candidates(ctx, source);
                var picks = ctx.Picks ?? [];
                if (picks.Count == 0) yield return (ApprovalMessages.PickLabel, []);
                foreach (var p in picks) yield return (p, allowed is null || allowed.Contains(p) ? [p] : []);
                break;
        }
    }

    /// <summary>候選群組的成員；沒有限定候選群組回 null（任何人都可）。</summary>
    internal static HashSet<string>? Candidates(ResolveContext ctx, ApproverSource source) =>
        source.CandidateGroupIds is { Count: > 0 } ids
            ? ids.SelectMany(id => ctx.GroupMembers.GetValueOrDefault(id) ?? []).ToHashSet(StringComparer.Ordinal)
            : null;

    private static string GroupLabel(ResolveContext ctx, long id) => ctx.GroupNames?.GetValueOrDefault(id) ?? $"#{id}";

    private static List<string> Clean(ResolveContext ctx, IEnumerable<string> users) =>
        users.Where(u => u != ctx.InitiatorUserId && ctx.Org.ActiveUsers.Contains(u)).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>備援名單：省略時是全部啟用中的系統管理員；一律移除發起人與停用帳號。</summary>
    private static List<string> FallbackUsers(ResolveContext ctx, ApproverSource? fallback)
    {
        if (fallback is null) return Clean(ctx, ctx.ActiveAdmins);
        return Clean(ctx, Units(ctx with { Picks = null }, fallback).SelectMany(u => u.Users));
    }
}
