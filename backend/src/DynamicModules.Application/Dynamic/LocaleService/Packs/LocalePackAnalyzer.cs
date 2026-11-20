using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Application.Dynamic.ImportService;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Localization;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.LocaleService.Packs;

/// <summary>
/// 語系包分析（語系包設計 §5.2、§6.2、§6.3；i4d1 計畫判斷 6、13、16、25）：純函式，不碰資料庫也不讀快照，輸入一律是呼叫端在交易內讀的
/// <see cref="LocaleWorld"/>。逐句驗證、和目前覆寫層比對（U5 逐句合併、Q6 與內建相同的不存）、完整度，以及是／否撞字（票 #68）：
/// 撞到的字來自這份包的 <c>report.Yes</c>／<c>report.No</c> 就拒收那一句（先拒「否」），拒收後重算到穩定；不是這份包造成的只列警告。
/// </summary>
public static class LocalePackAnalyzer
{
    public const string WarningBooleanCollisionExisting = "booleanCollisionExisting";
    private static readonly string[] BooleanKeys = [ReportText.No, ReportText.Yes];   // 拒收順序：先「否」
    private static readonly IReadOnlySet<string> NoNames = new HashSet<string>();
    private static readonly Lazy<HashSet<string>> UiBase = new(() => BuiltinLocaleResources.BaseKeys.ToHashSet(StringComparer.Ordinal));
    private static readonly Lazy<HashSet<string>> ServerBase = new(() => BuiltinServerText.BaseKeys.ToHashSet(StringComparer.Ordinal));

    public static LocalePackAnalysis Analyze(ParsedLocalePack pack, LocaleWorld world, int maxInstalled)
    {
        var code = pack.Code;
        var info = world.Installed.FirstOrDefault(i => i.Code == code);
        var isNew = info is null;
        var builtin = info?.Builtin ?? false;

        // ── 語系列 ──
        var changes = new List<string>();
        var ignored = new List<string>();
        LocalePackLocale? target = null;
        LocalePackLocale after;
        if (builtin)
        {
            if (pack.NativeName is not null && pack.NativeName != info!.NativeName) ignored.Add("nativeName");
            if (pack.QuasarLang is not null && pack.QuasarLang != info!.QuasarLang) ignored.Add("quasarLang");
            if (pack.Fallback is not null && pack.Fallback != info!.FallbackCode) ignored.Add("fallback");
            after = new LocalePackLocale(info!.NativeName, info.QuasarLang, info.FallbackCode);
        }
        else if (isNew)
        {
            if (pack.NativeName is null) return Fail(LocalePackMessages.NativeNameRequired);
            if (world.Installed.Count >= maxInstalled) return Fail(LocalePackMessages.MaxInstalled(maxInstalled));
            target = after = new LocalePackLocale(pack.NativeName, pack.QuasarLang, pack.Fallback);
            changes.Add("nativeName");
            if (pack.QuasarLang is not null) changes.Add("quasarLang");
            if (pack.Fallback is not null) changes.Add("fallback");
        }
        else
        {
            after = new LocalePackLocale(pack.NativeName ?? info!.NativeName, pack.QuasarLang ?? info!.QuasarLang, pack.Fallback ?? info!.FallbackCode);
            if (after.NativeName != info!.NativeName) changes.Add("nativeName");
            if (after.QuasarLang != info.QuasarLang) changes.Add("quasarLang");
            if (after.FallbackCode != info.FallbackCode) changes.Add("fallback");
            if (changes.Count > 0) target = after;
        }
        if (target is not null && (isNew || changes.Contains("fallback")) && Graph(InstalledAfter(world, code, isNew, target), code) is { } graphError)
            return Fail(graphError);

        // ── 句子 ──
        var current = world.Overlays.GetValueOrDefault(code);
        var ui = new SectionBuilder(pack.UiProblems);
        var uiBuiltin = BuiltinLocaleResources.Ui.GetValueOrDefault(code);
        foreach (var (key, text) in pack.Ui.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (ui.Prefilter(key, text, UiBase.Value)) continue;
            var reason = LocaleTextSafety.Check(text, LocalePackLimits.MaxTextLength)
                ?? UiMessageSyntax.Check(text, BuiltinLocaleResources.UiPlaceholderNames.GetValueOrDefault(key) ?? NoNames);
            if (reason is not null)
            {
                ui.Reject(key, reason, reason == LocalePackReasons.Placeholder ? UiAllowed(key) : []);
                continue;
            }
            ui.Compare(key, text, uiBuiltin?.GetValueOrDefault(key), current?.Ui);
        }

        var server = new SectionBuilder(pack.ServerProblems);
        var candidates = new Dictionary<string, BooleanCandidate>(StringComparer.Ordinal);
        var candidateKind = new Dictionary<string, Kind>(StringComparer.Ordinal);
        foreach (var (key, text) in pack.Server.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (server.Prefilter(key, text, ServerBase.Value)) continue;
            BuiltinServerText.TryGet(I18nText.FallbackLocale, key, out var zh);
            var reason = LocaleTextSafety.Check(text, LocalePackLimits.MaxTextLength) ?? ServerTemplateSyntax.Check(text, zh!);
            if (reason is not null)
            {
                server.Reject(key, reason, reason == LocalePackReasons.Placeholder ? ServerAllowed(zh!) : []);
                continue;
            }
            var own = BuiltinServerText.TryGet(code, key, out var b) ? b : null;
            var kind = server.Compare(key, text, own, current?.Server);
            if (!BooleanKeys.Contains(key) || kind is Kind.Unchanged || (kind is Kind.SameAsBuiltin && !server.Remove.Contains(key))) continue;
            // 是／否兩鍵先從計畫拿出來，篩選後才放回去
            server.Set.Remove(key);
            server.Remove.Remove(key);
            candidates[key] = new BooleanCandidate(text, kind is Kind.SameAsBuiltin);
            candidateKind[key] = kind;
        }

        var basePlan = new LocalePackPlan(code, isNew, target, ui.Set, ui.Remove.ToList(), server.Set, server.Remove.ToList(), [], candidates,
            new LocalePackInfo(pack.PackVersion, pack.BuiltFor, pack.BaseLocale, pack.Note));
        var (rejected, rejectedItems, collisions) = FilterBoolean(After(world, basePlan), code, candidates);
        var serverSet = new Dictionary<string, string>(server.Set, StringComparer.Ordinal);
        var serverRemove = server.Remove.ToList();
        foreach (var (key, c) in candidates)
        {
            if (rejected.Contains(key)) continue;
            if (c.Remove) serverRemove.Add(key);
            else serverSet[key] = c.Text;
        }
        foreach (var item in rejectedItems) server.MoveToRejected(item, candidateKind[item.Key]);
        serverRemove.Sort(StringComparer.Ordinal);

        var plan = basePlan with { ServerSet = serverSet, ServerRemove = serverRemove, BooleanRejected = rejected };
        var completeness = LocaleCompleteness.Of(code, After(world, plan).Overlays.GetValueOrDefault(code));
        var warnings = collisions.Collisions
            .Select(c => new LocalePackWarning(WarningBooleanCollisionExisting, [c.Word, string.Join(", ", c.YesLocales), string.Join(", ", c.NoLocales)]))
            .ToList();

        var report = new LocalePackReport(null, null, false, "", "",
            new LocaleReport(code, isNew, builtin, after.NativeName, after.QuasarLang, after.FallbackCode, changes, ignored),
            plan.Pack, ui.Report(), server.Report(), completeness, new DesignDataReport(pack.DesignDataCount), warnings);
        return new LocalePackAnalysis(report, plan, null);
    }

    public static LocalePackRecheck Recheck(LocalePackPlan plan, LocaleWorld world, int maxInstalled)
    {
        if (plan.IsNew && world.Installed.Count >= maxInstalled) return new(LocalePackMessages.MaxInstalled(maxInstalled), false);
        if (plan.Locale is { } target && Graph(InstalledAfter(world, plan.Code, plan.IsNew, target), plan.Code) is { } graphError)
            return new(graphError, false);
        var basePlan = plan with
        {
            ServerSet = plan.ServerSet.Where(kv => !BooleanKeys.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            ServerRemove = plan.ServerRemove.Where(k => !BooleanKeys.Contains(k)).ToList(),
        };
        var (rejected, _, _) = FilterBoolean(After(world, basePlan), plan.Code, plan.BooleanCandidates);
        return new(null, !rejected.ToHashSet(StringComparer.Ordinal).SetEquals(plan.BooleanRejected));
    }

    public static BooleanCollisionResult BooleanCollisions(LocaleWorld after, string? packCode)
    {
        var words = new List<(string Code, (string Text, bool Own) Yes, (string Text, bool Own) No)>();
        foreach (var info in after.Installed)
        {
            var chain = LocaleWorldText.ChainOf(after, info.Code);
            words.Add((info.Code, LocaleWorldText.Resolve(after, chain, LocaleWorldText.Server, ReportText.Yes),
                LocaleWorldText.Resolve(after, chain, LocaleWorldText.Server, ReportText.No)));
        }
        var ambiguous = BooleanWords.From(words.Select(w => (w.Yes.Text, w.No.Text))).Ambiguous;
        var collisions = new List<BooleanCollision>();
        foreach (var word in ambiguous.Order(StringComparer.Ordinal))
        {
            bool Same(string t) => string.Equals(BooleanWords.Normalize(t), word, StringComparison.OrdinalIgnoreCase);
            var packKeys = new List<string>();
            if (packCode is not null && words.FirstOrDefault(w => w.Code == packCode) is { Code: not null } mine)
            {
                if (mine.No.Own && Same(mine.No.Text)) packKeys.Add(ReportText.No);
                if (mine.Yes.Own && Same(mine.Yes.Text)) packKeys.Add(ReportText.Yes);
            }
            collisions.Add(new BooleanCollision(word, words.Where(w => Same(w.Yes.Text)).Select(w => w.Code).ToList(),
                words.Where(w => Same(w.No.Text)).Select(w => w.Code).ToList(), packKeys));
        }
        return new BooleanCollisionResult(collisions);
    }

    public static LocaleWorld After(LocaleWorld world, LocalePackPlan plan)
    {
        var installed = world.Installed.ToList();
        if (plan.Locale is { } l)
        {
            var row = new InstalledLocaleInfo(plan.Code, l.NativeName, l.QuasarLang, l.FallbackCode, false);
            var index = installed.FindIndex(i => i.Code == plan.Code);
            if (index >= 0) installed[index] = row;
            else installed.Add(row);
        }
        var current = world.Overlays.GetValueOrDefault(plan.Code);
        var overlays = new Dictionary<string, LocaleOverlay>(world.Overlays, StringComparer.Ordinal)
        {
            [plan.Code] = new LocaleOverlay(Merge(current?.Ui, plan.UiSet, plan.UiRemove), Merge(current?.Server, plan.ServerSet, plan.ServerRemove), current?.PackId ?? 0),
        };
        return world with { Installed = installed, Overlays = overlays };
    }

    // ── 內部 ──

    private static LocalePackAnalysis Fail(MessageRef error) => new(null, null, error);

    private static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string>? current, IReadOnlyDictionary<string, string> set, IReadOnlyList<string> remove)
    {
        var merged = current is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(current, StringComparer.Ordinal);
        foreach (var key in remove) merged.Remove(key);
        foreach (var (key, text) in set) merged[key] = text;
        return merged;
    }

    /// <summary>從候選值重跑撞字篩選（判斷 6）：每輪最多拒一句、先拒「否」，直到這份包不再造成撞字；只有兩句可拒，第三輪還要拒就是程式錯。</summary>
    private static (List<string> Rejected, List<RejectedItem> Items, BooleanCollisionResult Final) FilterBoolean(
        LocaleWorld baseAfter, string code, IReadOnlyDictionary<string, BooleanCandidate> candidates)
    {
        var rejected = new List<string>();
        var items = new List<RejectedItem>();
        for (var round = 0; ; round++)
        {
            var result = BooleanCollisions(WithCandidates(baseAfter, code, candidates, rejected), code);
            string? pick = null;
            BooleanCollision? hit = null;
            foreach (var key in BooleanKeys)
            {
                if (!candidates.ContainsKey(key) || rejected.Contains(key)) continue;
                hit = result.Collisions.FirstOrDefault(c => c.PackKeys.Contains(key));
                if (hit is not null) { pick = key; break; }
            }
            if (pick is null) return (rejected, items, result);
            if (round >= 2) throw new InvalidOperationException("是／否撞字的篩選沒有收斂");
            rejected.Add(pick);
            var others = (pick == ReportText.Yes ? hit!.NoLocales : hit!.YesLocales).Where(o => o != code).ToList();
            var other = others.FirstOrDefault() ?? (pick == ReportText.Yes ? hit.NoLocales : hit.YesLocales).FirstOrDefault() ?? "";
            items.Add(new RejectedItem(pick, LocalePackReasons.BooleanCollision, [hit.Word, other]));
        }
    }

    private static LocaleWorld WithCandidates(LocaleWorld baseAfter, string code, IReadOnlyDictionary<string, BooleanCandidate> candidates, List<string> rejected)
    {
        var current = baseAfter.Overlays.GetValueOrDefault(code);
        var server = current is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(current.Server, StringComparer.Ordinal);
        foreach (var (key, c) in candidates)
        {
            if (rejected.Contains(key)) continue;
            if (c.Remove) server.Remove(key);
            else server[key] = c.Text;
        }
        var overlays = new Dictionary<string, LocaleOverlay>(baseAfter.Overlays, StringComparer.Ordinal)
        {
            [code] = new LocaleOverlay(current?.Ui ?? new Dictionary<string, string>(StringComparer.Ordinal), server, current?.PackId ?? 0),
        };
        return baseAfter with { Overlays = overlays };
    }

    private static IReadOnlyList<(string Code, string? FallbackCode)> InstalledAfter(LocaleWorld world, string code, bool isNew, LocalePackLocale target)
    {
        var pairs = world.Installed.Select(i => (i.Code, i.Code == code ? target.FallbackCode : i.FallbackCode)).ToList();
        if (isNew) pairs.Add((code, target.FallbackCode));
        return pairs;
    }

    /// <summary>上層語系圖守衛的訊息對照（匯入與樣板匯出共用，i4d2 計畫判斷 9）。</summary>
    internal static MessageRef? Graph(IReadOnlyList<(string Code, string? FallbackCode)> installedAfter, string code) =>
        LocaleGraphRules.Check(installedAfter, code) switch
        {
            null => null,
            LocaleGraphRules.FallbackNotInstalled => LocalePackMessages.FallbackNotInstalled(installedAfter.First(p => p.Code == code).FallbackCode!),
            LocaleGraphRules.FallbackSelf => LocalePackMessages.FallbackSelf,
            LocaleGraphRules.FallbackCycle => LocalePackMessages.FallbackCycle(CyclePath(installedAfter, code)),
            _ => LocalePackMessages.FallbackTooDeep,
        };

    /// <summary>從目標沿上層走到第一個重複的語系（例 ja → ko → ja）。</summary>
    private static IEnumerable<string> CyclePath(IReadOnlyList<(string Code, string? FallbackCode)> pairs, string code)
    {
        var parents = pairs.ToDictionary(p => p.Code, p => p.FallbackCode, StringComparer.Ordinal);
        var path = new List<string> { code };
        var cur = code;
        while (parents.GetValueOrDefault(cur) is { } parent && parents.ContainsKey(parent))
        {
            path.Add(parent);
            if (path.IndexOf(parent) < path.Count - 1) break;
            cur = parent;
        }
        return path;
    }

    private static IReadOnlyList<string> UiAllowed(string key) =>
        (BuiltinLocaleResources.UiPlaceholderNames.GetValueOrDefault(key) ?? NoNames).Order(StringComparer.Ordinal).Select(n => "{" + n + "}").ToList();

    private static IReadOnlyList<string> ServerAllowed(string zh) =>
        System.Text.RegularExpressions.Regex.Matches(zh, @"\{(\d+)\}").Select(m => m.Value).Distinct().Order(StringComparer.Ordinal).ToList();

    private enum Kind { Added, Changed, Unchanged, SameAsBuiltin }

    /// <summary>一段的累計：筆數、要設與要拿掉的鍵、拒收清單。</summary>
    private sealed class SectionBuilder
    {
        private int _added, _changed, _unchanged, _skipped, _same, _extra;
        private readonly List<string> _extraKeys = [];
        private readonly List<RejectedItem> _rejected;

        public SectionBuilder(IReadOnlyList<RejectedItem> problems) => _rejected = [.. problems];

        public Dictionary<string, string> Set { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> Remove { get; } = new(StringComparer.Ordinal);

        /// <summary>空字串（略過）與基準以外的鍵（多鍵）先處理掉；處理掉回 true。</summary>
        public bool Prefilter(string key, string text, HashSet<string> baseKeys)
        {
            if (text.Length == 0) { _skipped++; return true; }
            if (baseKeys.Contains(key)) return false;
            _extra++;
            _extraKeys.Add(key);
            return true;
        }

        public void Reject(string key, string reason, IReadOnlyList<string> args) => _rejected.Add(new RejectedItem(key, reason, args));

        public Kind Compare(string key, string text, string? builtin, IReadOnlyDictionary<string, string>? current)
        {
            var had = current is not null && current.TryGetValue(key, out var old) ? old : null;
            if (builtin == text)
            {
                _same++;
                if (had is not null) Remove.Add(key);
                return Kind.SameAsBuiltin;
            }
            if (had == text) { _unchanged++; return Kind.Unchanged; }
            Set[key] = text;
            if (had is not null) { _changed++; return Kind.Changed; }
            _added++;
            return Kind.Added;
        }

        /// <summary>撞字拒收：從原本的分類移到拒收。</summary>
        public void MoveToRejected(RejectedItem item, Kind kind)
        {
            switch (kind)
            {
                case Kind.Added: _added--; break;
                case Kind.Changed: _changed--; break;
                case Kind.SameAsBuiltin: _same--; break;
            }
            _rejected.Add(item);
        }

        public SectionReport Report()
        {
            var rejected = _rejected.OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
            return new SectionReport(_added, _changed, _unchanged, _skipped, _same, _extra,
                _extraKeys.Order(StringComparer.Ordinal).Take(LocalePackLimits.MaxListedItems).ToList(),
                rejected.Count, rejected.Take(LocalePackLimits.MaxListedItems).ToList());
        }
    }
}
