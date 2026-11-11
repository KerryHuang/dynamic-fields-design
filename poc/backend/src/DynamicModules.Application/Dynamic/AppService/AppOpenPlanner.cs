using System.Text.RegularExpressions;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.AppService;

/// <summary>
/// 開啟前的所有檢查（唯讀）。預覽與開啟共用同一份，確保預覽講的就是開啟會遇到的。
/// 任何一項不通過，開啟就整個不做、什麼都不寫。
/// </summary>
public sealed partial class AppOpenPlanner(
    AppPackageSource packages, IAppRepository apps, IModuleRepository modules, IPermissionRepository permissions,
    IAccountRepository accounts, AppDependencyChecker dependencies, IConversionRepository conversions,
    IEventRepository events, ICheckRepository checks, SharedMasters.MasterSymbols symbols, SharedMasters.ContributionOrphans orphans)
{
    [GeneratedRegex(ModuleRules.ModuleKeyPattern)]
    private static partial Regex ModuleKey();

    /// <param name="MasterOrder">要先開啟或綁定的共用主檔 id，被依賴的在前（含經主檔間接需要的）。</param>
    public sealed record Plan(
        ServiceResult? Failure, SourcedPackage? Source, IReadOnlyDictionary<string, string> FinalKeys,
        IReadOnlyDictionary<string, string> ExternalModuleKeys, OpenPreview Preview, IReadOnlyDictionary<string, long> MappedGroups,
        IReadOnlyList<string>? MasterOrder = null)
    {
        public IReadOnlyList<string> MasterOrder { get; init; } = MasterOrder ?? [];
    }

    private static readonly OpenPreview Empty = new([], [], [], [], [], []);

    /// <param name="asMaster">由使用者套件自動開啟主檔時為 true；直接開啟主檔套件一律 409（主檔由使用它的應用程式管理）。</param>
    public async Task<Plan> PlanAsync(string packageId, OpenAppBody body, CancellationToken ct, bool asMaster = false)
    {
        var source = await packages.FindAsync(packageId, ct);
        if (source is null) return Fail(ServiceResult.NotFound(AppMessages.PackageNotFound));
        if (source.Package.Manifest.IsMaster && !asMaster) return Fail(ServiceResult.Conflict(AppMessages.MasterManaged));

        var existing = await apps.GetByKeyAsync(source.Package.Manifest.Id, ct);
        if (existing is not null)
            return Fail(ServiceResult.ConflictWith(
                existing.Status == AppStatus.Closed ? AppMessages.AlreadyClosedHint : AppMessages.AlreadyOpen,
                new { key = existing.Key, status = AppMapper.StatusName(existing.Status) }));

        var package = source.Package;
        // 主檔定義：目錄與匯入套件裡的主檔；引用的主檔欄位與選項都要找得到（共用主檔 §4.6）
        var knownMasters = (await packages.ListAsync(ct)).Where(k => k.Package.Manifest.IsMaster)
            .GroupBy(k => k.Package.Manifest.Id).ToDictionary(g => g.Key, g => g.First().Package);
        // 套件本身的錯誤（不看主檔）直接擋；對不上主檔（套件定義或資料庫裡現在的樣子）的引用算 unbound（§4.6、§9 最後一點），
        // 這樣「匯出的主檔少了被刪的基本欄位」和「已開啟的主檔被客戶改過」都回同一種 409
        var validation = AppPackageValidator.Validate(package, new Dictionary<string, AppPackageData>());
        var warnings = new List<string>(AppPackageValidator.Validate(package, knownMasters).Warnings);
        var errors = new List<string>(validation.Errors);
        if (errors.Count > 0)
            return new Plan(null, source, new Dictionary<string, string>(), new Dictionary<string, string>(), Empty with { Errors = errors }, new Dictionary<string, long>());

        // 模組 key：預設用檔案的，衝突時不自動改名（改名會讓靠 key 的引用悄悄指到別的模組），由管理員用 moduleKeys 指定
        foreach (var code in body.ModuleKeys.Keys.Where(c => package.Modules.All(m => m.Code != c)))
            errors.Add($"moduleKeys: 套件沒有模組 {code}");
        var finalKeys = new Dictionary<string, string>();
        var conflicts = new List<KeyConflict>();
        var taken = new HashSet<string>();
        foreach (var m in package.Modules)
        {
            var key = body.ModuleKeys.GetValueOrDefault(m.Code) ?? m.Key;
            finalKeys[m.Code] = key;
            if (!ModuleKey().IsMatch(key)) errors.Add($"moduleKeys.{m.Code}: 模組代碼 {key} 格式錯誤");
            else if (!taken.Add(key) || await modules.KeyExistsAsync(key, ct)) conflicts.Add(new KeyConflict(m.Code, key));
        }

        // 轉換代碼全站唯一；不自動改名（已轉出的單據靠它追溯）
        foreach (var c in package.Conversions)
            if (await conversions.GetByKeyAsync(c.Code, ct) is not null) errors.Add($"conversions.{c.Code}: 轉換代碼已被使用");

        // 事件與檢查的代碼同樣全站唯一（事實以事件鍵記帳，不自動改名）
        foreach (var e in package.Events)
            if (await events.GetByKeyAsync(e.Code, ct) is not null) errors.Add($"events.{e.Code}: 事件代碼已被使用");
        foreach (var c in package.Checks)
            if (await checks.GetByKeyAsync(c.Code, ct) is not null) errors.Add($"checks.{c.Code}: 檢查代碼已被使用");

        var hostConflicts = new List<string>();
        foreach (var m in package.Modules.Where(m => m.HostEntityKey is not null))
            if (await modules.HostEntityExistsAsync(m.HostEntityKey!, ct)) hostConflicts.Add(m.Code);

        var requires = RequiresOf(package);
        var requirement = await dependencies.CheckRequirementsAsync(requires, ct);
        var masters = await MastersAsync(package, knownMasters, ct);
        var unbound = await UnboundAsync(package, knownMasters, validation.Errors, ct);

        var existingGroups = await permissions.ListGroupsAsync(ct);
        var suggestions = package.Groups.Select(g =>
            existingGroups.FirstOrDefault(x => x.Key == g.Code) is { } hit
                ? new GroupSuggestion(g.Code, g.Name, "map", hit.Id)
                : new GroupSuggestion(g.Code, g.Name, "create", null)).ToList();

        var mapped = new Dictionary<string, long>();
        foreach (var (code, entry) in body.GroupMap)
        {
            var group = package.Groups.FirstOrDefault(g => g.Code == code);
            if (group is null) { errors.Add($"groupMap: 套件沒有群組 {code}"); continue; }
            switch (entry?.Mode)
            {
                case "map":
                    if (entry.GroupId is { } id && existingGroups.Any(g => g.Id == id)) mapped[code] = id;
                    else errors.Add($"groupMap.{code}: 對應的群組不存在");
                    break;
                case "create":
                    if (await accounts.GroupNameExistsAsync(body.Locale, group.Name.Resolve(body.Locale).Trim(), null, ct))
                        errors.Add($"groupMap.{code}: 已有同名的群組「{group.Name.Resolve(body.Locale)}」");
                    break;
                case "skip":
                    break;
                default:
                    errors.Add($"groupMap.{code}: mode 必須是 map、create 或 skip");
                    break;
            }
        }
        foreach (var g in package.Groups.Where(g => !body.GroupMap.ContainsKey(g.Code)))
            warnings.Add($"群組「{g.Code}」沒有對應，已略過（不給任何人權限）");
        // 同一個應用程式重開：型別不同、不會接回的孤兒欄位先在預覽警告（§7 刪除第 6 點）
        if (package.Contributions.Count > 0) warnings.AddRange((await orphans.MatchAsync(package, ct)).Warnings);
        warnings.AddRange(await TasksAutoEnableAsync(package, knownMasters, ct));

        return new Plan(null, source, finalKeys, requirement.ExternalModuleKeys,
            new OpenPreview(errors, conflicts, hostConflicts, [.. requirement.Missing, .. masters.Missing], suggestions, warnings,
                masters.Previews, unbound), mapped, masters.Order);
    }

    private sealed record MasterPlan(List<string> Order, List<MissingDependency> Missing, List<MasterPreview> Previews);

    /// <summary>
    /// requires.masters 的主檔，連同主檔自己的 requires.masters，依相依順序（被依賴的在前）。已開啟的直接綁定、已關閉的重開；
    /// 還沒有的要在目錄或匯入套件裡找到主檔套件，否則 masterNotAvailable。
    /// </summary>
    private async Task<MasterPlan> MastersAsync(AppPackageData package, IReadOnlyDictionary<string, AppPackageData> known, CancellationToken ct)
    {
        var plan = new MasterPlan([], [], []);
        var visiting = new HashSet<string>();
        var existingGroups = await permissions.ListGroupsAsync(ct);
        async Task VisitAsync(string id)
        {
            if (plan.Order.Contains(id) || !visiting.Add(id)) return;
            var app = await apps.GetByKeyAsync(id, ct);
            List<string> requires;
            if (app is { Kind: AppKind.Master })
            {
                requires = app.Requires.Masters.Select(r => r.Id).ToList();
                plan.Previews.Add(new MasterPreview(id, app.NameI18n, app.Status == AppStatus.Open ? "bind" : "reopen", []));
            }
            else if (app is null && known.TryGetValue(id, out var master))
            {
                requires = master.Manifest.Requires.Masters.Select(r => r.Id).ToList();
                plan.Previews.Add(new MasterPreview(id, master.Manifest.Name, "open", master.Groups.Select(g =>
                    existingGroups.FirstOrDefault(x => x.Key == g.Code) is { } hit
                        ? new GroupSuggestion(g.Code, g.Name, "map", hit.Id)
                        : new GroupSuggestion(g.Code, g.Name, "create", null)).ToList()));
            }
            else
            {
                plan.Missing.Add(new MissingDependency("master", id, null, null, AppMessages.MasterNotAvailable));
                return;
            }
            foreach (var dep in requires) await VisitAsync(dep);
            plan.Order.Add(id);
        }
        foreach (var r in package.Manifest.Requires.Masters) await VisitAsync(r.Id);
        // 預覽依開啟順序列（被依賴的在前）
        plan.Previews.Sort((a, b) => plan.Order.IndexOf(a.Id).CompareTo(plan.Order.IndexOf(b.Id)));
        return plan;
    }

    /// <summary>
    /// 套件引用的主檔符號對不上：用主檔的定義（已開啟的用資料庫裡現在的樣子，客戶刪過或改過型別的基本欄位會在這裡現形；
    /// 沒開啟的用目錄與匯入的那份）再驗一次，比不看主檔時多出來的錯誤就是 unbound（§4.6），列出對不上的符號。
    /// </summary>
    private async Task<List<string>> UnboundAsync(
        AppPackageData package, IReadOnlyDictionary<string, AppPackageData> known, IReadOnlyList<string> baseline, CancellationToken ct)
    {
        var live = new Dictionary<string, AppPackageData>(known);
        foreach (var r in package.Manifest.Requires.Masters)
            if (await apps.GetByKeyAsync(r.Id, ct) is { Kind: AppKind.Master } app)
                live[r.Id] = await symbols.DefinitionAsync(app, known.GetValueOrDefault(r.Id), ct);
        return AppPackageValidator.Validate(package, live).Errors.Except(baseline).ToList();
    }

    /// <summary>
    /// 任務規則所在的模組關閉任務時，開啟會自動打開（<see cref="AppFlowInstaller.InstallTaskRulesAsync"/>），預覽先列出（任務設計 §4.4）。
    /// 本套件的模組看套件；主檔模組已開啟的看資料庫現在的開關，還沒開啟的看主檔套件。每個模組只列第一條規則。
    /// </summary>
    private async Task<List<string>> TasksAutoEnableAsync(AppPackageData package, IReadOnlyDictionary<string, AppPackageData> known, CancellationToken ct)
    {
        var result = new List<string>();
        var seen = new HashSet<string>();
        foreach (var r in package.TaskRules.Where(r => r is not null))
        {
            var target = r.Module ?? "";
            (bool Enabled, I18nText Name)? state = null;
            if (AppPackageValidator.MasterScope.Parse(target) is { } symbol)
            {
                if (await apps.GetByKeyAsync(symbol.App, ct) is { Kind: AppKind.Master } master)
                {
                    if (master.Modules.FirstOrDefault(m => m.Code == symbol.Module) is { } member
                        && await modules.GetByIdAsync(member.ModuleId, includeChildren: false, ct) is { } module)
                        state = (module.TasksEnabled, module.NameI18n);
                }
                else if (known.GetValueOrDefault(symbol.App)?.Modules.FirstOrDefault(m => m.Code == symbol.Module) is { } declared)
                    state = (declared.TasksEnabled != false, declared.Name);
            }
            else if (package.Modules.FirstOrDefault(m => m.Code == target) is { } local)
                state = (local.TasksEnabled != false, local.Name);
            if (state is { Enabled: false } off && seen.Add(target))
                result.Add(AppFlowInstaller.TaskRuleWillAutoEnable(r.Code, off.Name.Resolve(I18nText.FallbackLocale)));
        }
        return result;
    }

    public static AppRequires RequiresOf(AppPackageData package) => new()
    {
        Apps = package.Manifest.Requires.Apps.Select(a => new AppRequirement { Id = a.Id, Modules = [.. a.Modules] }).ToList(),
        Entities = [.. package.Manifest.Requires.Entities],
        Masters = package.Manifest.Requires.Masters.Select(a => new AppRequirement { Id = a.Id, Modules = [.. a.Modules] }).ToList()
    };

    private static Plan Fail(ServiceResult failure) =>
        new(failure, null, new Dictionary<string, string>(), new Dictionary<string, string>(), Empty, new Dictionary<string, long>());
}
