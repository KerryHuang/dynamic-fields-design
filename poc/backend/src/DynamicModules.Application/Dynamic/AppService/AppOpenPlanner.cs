using System.Text.RegularExpressions;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.AppService;

/// <summary>
/// 開啟前的所有檢查（唯讀）。預覽與開啟共用同一份，確保預覽講的就是開啟會遇到的。
/// 任何一項不通過，開啟就整個不做、什麼都不寫。
/// </summary>
public sealed partial class AppOpenPlanner(
    AppPackageSource packages, IAppRepository apps, IModuleRepository modules, IPermissionRepository permissions,
    IAccountRepository accounts, AppDependencyChecker dependencies)
{
    [GeneratedRegex(ModuleRules.ModuleKeyPattern)]
    private static partial Regex ModuleKey();

    public sealed record Plan(
        ServiceResult? Failure, SourcedPackage? Source, IReadOnlyDictionary<string, string> FinalKeys,
        IReadOnlyDictionary<string, string> ExternalModuleKeys, OpenPreview Preview, IReadOnlyDictionary<string, long> MappedGroups);

    private static readonly OpenPreview Empty = new([], [], [], [], [], []);

    public async Task<Plan> PlanAsync(string packageId, OpenAppBody body, CancellationToken ct)
    {
        var source = await packages.FindAsync(packageId, ct);
        if (source is null) return Fail(ServiceResult.NotFound(AppMessages.PackageNotFound));

        var existing = await apps.GetByKeyAsync(source.Package.Manifest.Id, ct);
        if (existing is not null)
            return Fail(ServiceResult.ConflictWith(
                existing.Status == AppStatus.Closed ? AppMessages.AlreadyClosedHint : AppMessages.AlreadyOpen,
                new { key = existing.Key, status = AppMapper.StatusName(existing.Status) }));

        var package = source.Package;
        var validation = AppPackageValidator.Validate(package);
        var warnings = new List<string>(validation.Warnings);
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

        var hostConflicts = new List<string>();
        foreach (var m in package.Modules.Where(m => m.HostEntityKey is not null))
            if (await modules.HostEntityExistsAsync(m.HostEntityKey!, ct)) hostConflicts.Add(m.Code);

        var requires = RequiresOf(package);
        var requirement = await dependencies.CheckRequirementsAsync(requires, ct);

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

        return new Plan(null, source, finalKeys, requirement.ExternalModuleKeys,
            new OpenPreview(errors, conflicts, hostConflicts, requirement.Missing, suggestions, warnings), mapped);
    }

    public static AppRequires RequiresOf(AppPackageData package) => new()
    {
        Apps = package.Manifest.Requires.Apps.Select(a => new AppRequirement { Id = a.Id, Modules = [.. a.Modules] }).ToList(),
        Entities = [.. package.Manifest.Requires.Entities]
    };

    private static Plan Fail(ServiceResult failure) =>
        new(failure, null, new Dictionary<string, string>(), new Dictionary<string, string>(), Empty, new Dictionary<string, long>());
}
