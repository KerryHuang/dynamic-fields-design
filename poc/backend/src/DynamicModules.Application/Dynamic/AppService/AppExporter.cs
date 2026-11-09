using System.Text.RegularExpressions;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.AppService.Integrations;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.AppService;

/// <summary>
/// 由目前的成員模組重新產生套件（開啟後內容屬於客戶，不保留開啟時的原文）。
/// 表達不了的引用不匯出並記在 warnings（和範本匯出一致）；匯出的檔案一定要能被 v3 讀取器完整讀回。
/// </summary>
public sealed partial class AppExporter(
    TemplateExporter templates, IModuleRepository modules, IAppRepository apps, IPermissionRepository permissions,
    IConversionRepository conversions, IReportRepository reports, IPrintTemplateRepository printTemplates,
    IEventRepository events, ICheckRepository checks, ILedgerRepository ledgers, ISharedMasterRepository shared,
    AppPackageSource packages)
{
    [GeneratedRegex("[^a-zA-Z0-9_]")]
    private static partial Regex NotCode();

    public sealed record Exported(AppPackageData Package, List<string> Warnings);

    /// <summary>
    /// 使用者套件看得到的一個主檔模組（共用主檔 §9）：主檔名下的基本欄位加上本應用程式名下的貢獻（對照列含 /），
    /// 用主檔符號 &lt;主檔 id&gt;/&lt;模組代號&gt; 匯出一次。Names 給使用者模組的帶值、守衛、轉換、報表解析主檔符號；
    /// Module 是匯出結果（只用來挑出本應用程式的貢獻欄位）。
    /// </summary>
    private sealed record MasterView(
        App Master, string MemberCode, Module Source, string Symbol, TemplateModule Module, AppReportCodec.Names Names,
        Dictionary<long, string> OwnFields, Dictionary<long, string> OwnBlocks);

    public async Task<Exported> ExportAsync(App app, CancellationToken ct)
    {
        var isMaster = app.Kind == AppKind.Master;
        var all = await modules.ListAsync(ct);
        var members = new List<Module>();
        foreach (var member in app.Modules.OrderBy(m => m.Code, StringComparer.Ordinal))
            if (await modules.GetByIdAsync(member.ModuleId, includeChildren: true, ct) is { } m) members.Add(m);
        var codeOfModule = app.Modules.ToDictionary(m => m.ModuleId, m => m.Code);
        var keyToCode = members.ToDictionary(m => m.Key, m => codeOfModule[m.Id]);

        // 別的應用程式（含共用主檔）的模組：key → (模組代號, 應用程式 id, 是不是主檔)
        var allApps = await apps.ListAsync(ct);
        var moduleKeyById = all.ToDictionary(m => m.Id, m => m.Key);
        var external = new Dictionary<string, (string Code, string App, bool Master)>();
        foreach (var other in allApps.Where(a => a.Id != app.Id))
            foreach (var m in other.Modules.Where(m => moduleKeyById.ContainsKey(m.ModuleId)))
                external[moduleKeyById[m.ModuleId]] = (m.Code, other.Key, other.Kind == AppKind.Master);
        // 主檔歸 requires.masters、不進 requires.apps（共用主檔 §9，第二輪必修 1）
        var usedApps = new Dictionary<string, HashSet<string>>();
        var usedMasters = new Dictionary<string, HashSet<string>>();
        static void Use(Dictionary<string, HashSet<string>> used, string id, string code)
        {
            if (!used.TryGetValue(id, out var set)) used[id] = set = [];
            set.Add(code);
        }
        (string Code, string? App)? Target(string key, bool record)
        {
            if (keyToCode.TryGetValue(key, out var code)) return (code, null);
            if (!external.TryGetValue(key, out var ext)) return null;
            if (record) Use(ext.Master ? usedMasters : usedApps, ext.App, ext.Code);
            return (ext.Code, ext.App);
        }
        (string Code, string? App)? RefTarget(string key) => Target(key, record: true);

        var bindings = await apps.GetBindingsAsync(app.Id, ct);
        var warnings = new List<string>();
        var views = isMaster ? [] : await MasterViewsAsync(app, allApps, bindings, key => Target(key, record: false), ct);
        var masterNames = views.Count == 0 ? null : views.Select(v => v.Names).Aggregate(AppReportCodec.Names.Or);

        // 整合建立的欄位、事件、檢查不進一般 section（它們屬於 integrations 宣告）；其餘欄位、區塊、選項保留開啟時的代號
        var integrationRows = (await apps.ListIntegrationsAsync(ct)).Where(r => r.AppId == app.Id).ToList();
        var integrationItems = integrationRows.Select(r => IntegrationItems.Parse(r.ItemsJson)).ToList();
        var excludedFields = integrationItems.SelectMany(i => i.Fields.Select(f => f.FieldId)).ToHashSet();
        var excludedEvents = integrationItems.SelectMany(i => i.Events).ToHashSet();
        var excludedChecks = integrationItems.SelectMany(i => i.Checks).ToHashSet();
        // 主檔匯出依歸屬：貢獻欄位與它們的區塊屬於貢獻者，孤兒沒有任何一份套件擁有，都不匯出（孤兒列在警告）
        var foreignBlocks = new HashSet<long>();
        if (isMaster)
        {
            var orphans = await shared.ListOrphansAsync(null, ct);
            foreach (var m in members)
            {
                excludedFields.UnionWith((await shared.ListContributedFieldsAsync(m.Id, ct)).Select(f => f.FieldId));
                foreignBlocks.UnionWith((await shared.ListBlockOwnersAsync(m.Id, ct)).Select(b => b.BlockId));
                foreach (var f in m.Blocks.SelectMany(b => b.Fields).Where(f => orphans.Any(o => o.FieldId == f.Id)))
                {
                    excludedFields.Add(f.Id);
                    warnings.Add($"{m.NameI18n.Resolve(I18nText.FallbackLocale)}的「{f.LabelI18n.Resolve(I18nText.FallbackLocale)}」是已刪除應用程式留下的停用欄位，未匯出，資料仍保留在資料庫");
                }
            }
        }
        var codes = ExportCodeBook(members, codeOfModule, bindings, excludedFields);
        var exported = await templates.ExportMembersAsync(members, m => codeOfModule[m.Id], RefTarget, ct, codes,
            includeInactive: isMaster, external: masterNames);
        warnings.AddRange(exported.Warnings);
        var names = masterNames is null ? exported.Names : AppReportCodec.Names.Or(exported.Names, masterNames);

        // 群組：Key 當代號（自動產生的 g-xxxx 含連字號，換成合法代號）；用到才輸出。模組權限、貢獻權限、欄位例外共用這一個（第二輪必修 2）
        var groupList = (await permissions.ListGroupsAsync(ct)).OrderBy(g => g.Id).ToList();
        var codeOfGroup = new Dictionary<long, string>();
        var usedCodes = new HashSet<string>();
        string GroupCode(long id)
        {
            if (codeOfGroup.TryGetValue(id, out var existing)) return existing;
            var key = groupList.First(g => g.Id == id).Key;
            var code = NotCode().Replace(key, "_");
            if (code.Length == 0 || !char.IsAsciiLetterLower(code[0])) code = "g" + code;
            code = code.Length > 40 ? code[..40] : code;
            for (var i = 2; !usedCodes.Add(code); i++) code = (code.Length > 36 ? code[..36] : code) + "_" + i;
            return codeOfGroup[id] = code;
        }
        var groupIdOfKey = groupList.ToDictionary(g => g.Key, g => g.Id);

        // 模組權限裡的群組 Key 換成代號；主檔模組的權限只帶主檔自己授予的（steward），不是重算後的有效值
        var declared = isMaster ? (await packages.FindAsync(app.Key, ct))?.Package : null;
        var masterGroups = new Dictionary<long, HashSet<long>>();
        var moduleList = new List<TemplateModule>();
        foreach (var (m, module) in exported.Modules.Select(x => (members.First(mm => codeOfModule[mm.Id] == x.Code), x)))
        {
            if (!isMaster)
            {
                moduleList.Add(module with
                {
                    Permissions = module.Permissions.Where(p => groupIdOfKey.ContainsKey(p.Group))
                        .Select(p => p with { Group = GroupCode(groupIdOfKey[p.Group]) }).ToList()
                });
                continue;
            }
            var own = (await shared.ListGrantsAsync(m.Id, ct)).Where(g => g.SourceKind == GrantSource.Master && g.AppId == app.Id).ToList();
            masterGroups[m.Id] = own.Select(g => g.GroupId).ToHashSet();
            var blockCodes = m.Blocks.Where(b => foreignBlocks.Contains(b.Id)).Select(b => codes.Block(b.Id)).ToHashSet();
            moduleList.Add(module with
            {
                Blocks = module.Blocks.Where(b => !blockCodes.Contains(b.Code)).ToList(),
                Permissions = GrantPermissions(own, GroupCode, $"模組「{module.Code}」", warnings),
                Extensible = declared?.Modules.FirstOrDefault(x => x.Code == module.Code)?.Extensible ?? true
            });
        }

        var fieldAccess = new List<AppFieldAccess>();
        var optionScopes = new List<AppOptionScope>();
        foreach (var m in members)
        {
            var rows = await permissions.LoadModuleRowsAsync(m, ct);
            foreach (var p in rows.FieldPermissions)
                if (exported.Names.Field(p.FieldId) is { } field && groupList.Any(g => g.Id == p.GroupId)
                    && (!isMaster || masterGroups[m.Id].Contains(p.GroupId)))
                    fieldAccess.Add(new AppFieldAccess(field, GroupCode(p.GroupId), p.Access));
            foreach (var scope in rows.OptionScopes.GroupBy(o => o.OptionId))
            {
                var module = codeOfModule[m.Id];
                // 選項所屬欄位必須實際匯出（沒停用、沒被省略），否則 optionScopes 會指到檔案裡不存在的選項，重新匯入時被拒
                var owner = m.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Options.Any(o => o.Id == scope.Key && o.Active));
                var option = owner?.Options.First(o => o.Id == scope.Key);
                if (owner is null || option is null || exported.Names.Option(owner.Id, option.Id) is null || exported.Names.Field(owner.Id) is null) continue;
                var scopeGroups = scope.Select(s => s.GroupId).Distinct().Where(id => groupList.Any(g => g.Id == id)).Select(GroupCode).ToList();
                // 範圍指向的群組都已不存在（殘留資料）：匯出空範圍會被匯入驗證退回，所以不匯出並告知
                if (scopeGroups.Count == 0)
                {
                    warnings.Add($"選項「{module}.{codes.Option(option.Id)}」的限定群組都已不存在，選項範圍未匯出");
                    continue;
                }
                optionScopes.Add(new AppOptionScope($"{module}.{codes.Option(option.Id)}", scopeGroups));
            }
        }

        var contributions = new List<AppContribution>();
        foreach (var view in views)
            if (await ContributionOfAsync(app, view, GroupCode, warnings, ct) is { } contribution)
            {
                contributions.Add(contribution);
                foreach (var f in contribution.Fields.Where(f => f.Type == FieldType.Reference))
                    if (TemplateGraph.ConfigObject(f.Config, "refTarget") is { } t && TemplateGraph.ConfigString(t, "app") is { } refApp
                        && TemplateGraph.ConfigString(t, "key") is { } refKey)
                        Use(usedMasters, refApp, refKey);
            }

        var masterTargets = views.ToDictionary(v => v.Source.Id, v => v.Symbol);
        var workflows = ExportWorkflows(members, codeOfModule, names, GroupCode, warnings);
        var conversionList = await ExportConversionsAsync(members, codeOfModule, masterTargets, names, warnings, ct);
        var reportList = await ExportReportsAsync(app, names, GroupCode, warnings, ct);
        var printList = await ExportPrintTemplatesAsync(members, codeOfModule, keyToCode, names, warnings, ct);
        var ledgerList = await ExportLedgersAsync(members, codeOfModule, names, warnings, ct);
        var eventList = await ExportEventsAsync(members, keyToCode, names, warnings, excludedEvents, ct);
        var checkList = await ExportChecksAsync(members, codeOfModule, names, warnings, excludedChecks, ct);
        if (isMaster && workflows.Count + conversionList.Count + printList.Count + ledgerList.Count + eventList.Count + checkList.Count > 0)
        {
            // 主檔套件不收流程（§4.1）：客戶在設計器加的就留在資料庫，不寫進檔案，否則匯出的檔案會被匯入驗證退回
            warnings.Add("共用主檔不帶狀態機、轉換、連動事件、跨記錄檢查、分錄與列印範本，這些設定未匯出");
            workflows = []; conversionList = []; printList = []; ledgerList = []; eventList = []; checkList = [];
        }

        var entities = exported.Modules.Where(m => m.HostEntityKey is not null).Select(m => m.HostEntityKey!)
            .Concat(exported.Modules.SelectMany(m => m.Blocks).SelectMany(b => b.Fields)
                .Where(f => f.Type == FieldType.Reference)
                .Select(f => TemplateGraph.ConfigObject(f.Config, "refTarget"))
                .Where(t => t is not null && TemplateGraph.ConfigString(t.Value, "kind") == ReferenceTarget.EntityKind)
                .Select(t => TemplateGraph.ConfigString(t!.Value, "key")!))
            .Distinct().Order(StringComparer.Ordinal).ToList();
        // requires.masters：開啟時的宣告加上匯出時實際引用到的主檔
        foreach (var r in app.Requires.Masters)
            foreach (var code in r.Modules.DefaultIfEmpty()) if (code is null) { if (!usedMasters.ContainsKey(r.Id)) usedMasters[r.Id] = []; } else Use(usedMasters, r.Id, code);
        static List<AppRequirementData> Requirements(Dictionary<string, HashSet<string>> used) =>
            used.OrderBy(a => a.Key, StringComparer.Ordinal)
                .Select(a => new AppRequirementData(a.Key, a.Value.Order(StringComparer.Ordinal).ToList())).ToList();
        var requires = new AppManifestRequires(Requirements(usedApps), entities, Requirements(usedMasters));

        var groups = codeOfGroup.OrderBy(g => g.Key)
            .Select(g => new AppGroup(g.Value, groupList.First(x => x.Id == g.Key).NameI18n)).ToList();
        var package = new AppPackageData(
            AppPackageData.FormatName, AppPackageData.CurrentFormatVersion,
            new AppManifest(app.Key, app.PackageVersion, app.NameI18n, null, app.Icon, null, requires, isMaster ? AppManifest.MasterKindName : null),
            groups, moduleList, fieldAccess, optionScopes, reportList, null, workflows, conversionList, eventList, printList,
            checkList, ledgerList, IntegrationDeclarations.Parse(app.IntegrationsJson), contributions);
        // groups 在上面各步驟用到才加進 codeOfGroup，最後才組出來
        return new Exported(package, warnings);
    }

    /// <summary>應用程式直接用到的每個主檔模組，各匯出一次（基本欄位＋本應用程式的貢獻，停用的也算）。</summary>
    private async Task<List<MasterView>> MasterViewsAsync(
        App app, IReadOnlyList<App> allApps, AppBindings own, Func<string, (string Code, string? App)?> target, CancellationToken ct)
    {
        var views = new List<MasterView>();
        foreach (var use in (await shared.ListUsesAsync(ct)).Where(u => u.AppId == app.Id))
        {
            if (allApps.FirstOrDefault(a => a.Id == use.MasterAppId) is not { } master) continue;
            var baseBindings = await apps.GetBindingsAsync(master.Id, ct);
            foreach (var member in master.Modules.OrderBy(m => m.Code, StringComparer.Ordinal))
            {
                if (await modules.GetByIdAsync(member.ModuleId, includeChildren: true, ct) is not { } module) continue;
                var symbol = $"{master.Key}/{member.Code}";
                var book = ContributionCodeBook(module, $"{member.Code}.", $"{symbol}.", baseBindings, own);
                var exported = await templates.ExportMembersAsync([module], _ => symbol, target, ct, book.Codes, includeInactive: true);
                views.Add(new MasterView(master, member.Code, module, symbol, exported.Modules.Single(), exported.Names, book.OwnFields, book.OwnBlocks));
            }
        }
        return views;
    }

    /// <summary>
    /// 主檔模組上的代號：基本的用主檔名下的對照列、本應用程式的貢獻用自己名下的（含 /）；別人的貢獻與孤兒不匯出。
    /// 沒有對照列的選項退回 o_&lt;id&gt;，已佔用集合先放入基本代號，退回的代號不會和基本代號相同（§5.4 撞名第 3 點）。
    /// </summary>
    private static (ExportCodes Codes, Dictionary<long, string> OwnFields, Dictionary<long, string> OwnBlocks) ContributionCodeBook(
        Module module, string basePrefix, string ownPrefix, AppBindings master, AppBindings own)
    {
        static Dictionary<long, string> Pick(IReadOnlyDictionary<string, long> names, string prefix, bool contributed) =>
            names.Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal) && x.Key.Contains('/') == contributed)
                .GroupBy(x => x.Value).ToDictionary(g => g.Key, g => g.First().Key[prefix.Length..]);
        var ownFields = Pick(own.Fields, ownPrefix, true);
        var ownBlocks = Pick(own.Blocks, ownPrefix, true);
        var fields = Pick(master.Fields, basePrefix, false);
        foreach (var (id, code) in ownFields) fields[id] = code;
        var blocks = Pick(master.Blocks, basePrefix, false);
        foreach (var (id, code) in ownBlocks) blocks[id] = code;
        var options = Pick(master.Options, basePrefix, false);
        foreach (var (id, code) in Pick(own.Options, ownPrefix, true)) options[id] = code;
        var taken = options.Values.ToHashSet(StringComparer.Ordinal);
        foreach (var o in module.Blocks.SelectMany(b => b.Fields).Where(f => fields.ContainsKey(f.Id)).SelectMany(f => f.Options))
        {
            if (options.ContainsKey(o.Id)) continue;
            var candidate = $"o_{o.Id}";
            for (var i = 2; !taken.Add(candidate); i++) candidate = $"o_{o.Id}_{i}";
            options[o.Id] = candidate;
        }
        var excluded = module.Blocks.SelectMany(b => b.Fields).Select(f => f.Id).Where(id => !fields.ContainsKey(id)).ToHashSet();
        return (new ExportCodes(fields, blocks, options, excluded), ownFields, ownBlocks);
    }

    /// <summary>
    /// 從資料庫重建一筆貢獻（§9）：本應用程式名下的區塊與欄位（含設計器加在它區塊裡的、停用的），反映客戶事後的修改；
    /// 權限只帶它自己授予的（Dynamic_AppGrant 來源為它），欄位例外只帶它的欄位、它的群組。沒有任何貢獻時回 null。
    /// </summary>
    private async Task<AppContribution?> ContributionOfAsync(
        App app, MasterView view, Func<long, string> groupCode, List<string> warnings, CancellationToken ct)
    {
        if (view.OwnFields.Count == 0 && view.OwnBlocks.Count == 0) return null;
        var ownCodes = view.OwnFields.Values.ToHashSet(StringComparer.Ordinal);
        var fields = view.Module.Blocks.SelectMany(b => b.Fields).Where(f => ownCodes.Contains(f.Code)).ToList();
        foreach (var f in view.Source.Blocks.SelectMany(b => b.Fields)
                     .Where(f => view.OwnFields.ContainsKey(f.Id) && fields.All(x => x.Code != view.OwnFields[f.Id])))
            warnings.Add($"貢獻欄位「{f.LabelI18n.Resolve(I18nText.FallbackLocale)}」引用了停用、未匯出或別的應用程式的欄位，未匯出");
        var (blockId, blockCode) = view.OwnBlocks.Count > 0 ? (view.OwnBlocks.First().Key, view.OwnBlocks.First().Value) : (0L, "main");
        var block = view.Source.Blocks.FirstOrDefault(b => b.Id == blockId);
        var grants = (await shared.ListGrantsAsync(view.Source.Id, ct)).Where(g => g.SourceKind == GrantSource.App && g.AppId == app.Id).ToList();
        var groups = grants.Select(g => g.GroupId).ToHashSet();
        var access = new List<AppFieldAccess>();
        foreach (var p in (await permissions.LoadModuleRowsAsync(view.Source, ct)).FieldPermissions)
            if (view.OwnFields.ContainsKey(p.FieldId) && groups.Contains(p.GroupId) && view.Names.Field(p.FieldId) is { } symbol)
                access.Add(new AppFieldAccess(symbol, groupCode(p.GroupId), p.Access));
        return new AppContribution(view.Master.Key, view.MemberCode,
            new AppContributionBlock(blockCode, block?.LabelI18n ?? app.NameI18n), fields,
            GrantPermissions(grants, groupCode, $"貢獻「{view.Symbol}」", warnings), access);
    }

    /// <summary>
    /// 權限來源記錄（一個動作一列）組回套件的群組權限。套件一個群組只有一個範圍：各動作範圍不同時取最窄的（只收緊，不放寬）並警告。
    /// </summary>
    private static List<TemplatePermission> GrantPermissions(
        IEnumerable<AppGrant> grants, Func<long, string> groupCode, string where, List<string> warnings)
    {
        var result = new List<TemplatePermission>();
        foreach (var g in grants.GroupBy(x => x.GroupId).OrderBy(g => g.Key))
        {
            var actions = g.Select(x => x.Action).ToHashSet();
            var scope = g.Min(x => x.Scope);
            var code = groupCode(g.Key);
            if (g.Select(x => x.Scope).Distinct().Count() > 1)
                warnings.Add($"{where}的群組「{code}」各動作的資料範圍不同，套件一個群組只能有一個範圍，匯出時取最窄的");
            result.Add(new TemplatePermission(code, new PermissionService.Data.PrivilegesDto(
                actions.Contains(PermissionAction.Read), actions.Contains(PermissionAction.Add), actions.Contains(PermissionAction.Edit),
                actions.Contains(PermissionAction.Delete), actions.Contains(PermissionAction.PrintExport), actions.Contains(PermissionAction.Pricing),
                actions.Contains(PermissionAction.Approve)), scope));
        }
        return result;
    }

    /// <summary>
    /// 匯出用的代號：有對照列的欄位、區塊、選項用開啟時的代號；沒有的（開啟後客戶新增）退回 f_／b_／o_ 加 Id。
    /// 每個模組只有一份避撞對照：先登記所有保留的原始代號，回退代號撞到就加 _2、_3，欄位定義與所有引用共用同一份。
    /// </summary>
    internal static ExportCodes ExportCodeBook(
        List<Module> members, Dictionary<long, string> codeOfModule, AppBindings bindings, HashSet<long> excluded)
    {
        var fields = new Dictionary<long, string>();
        var blocks = new Dictionary<long, string>();
        var options = new Dictionary<long, string>();
        foreach (var m in members)
        {
            var prefix = codeOfModule[m.Id] + ".";
            string Rest(string name) => name[prefix.Length..];
            var fieldList = m.Blocks.SelectMany(b => b.Fields).ToList();
            var boundField = bindings.Fields.Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(x => x.Value, x => Rest(x.Key));
            var boundBlock = bindings.Blocks.Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(x => x.Value, x => Rest(x.Key));
            var boundOption = bindings.Options.Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(x => x.Value, x => Rest(x.Key));

            Assign(fieldList.Select(f => f.Id), boundField, "f_", fields);
            Assign(m.Blocks.Select(b => b.Id), boundBlock, "b_", blocks);
            Assign(fieldList.SelectMany(f => f.Options.Select(o => o.Id)), boundOption, "o_", options);
        }
        return new ExportCodes(fields, blocks, options, excluded);

        static void Assign(IEnumerable<long> ids, Dictionary<long, string> bound, string fallbackPrefix, Dictionary<long, string> target)
        {
            var list = ids.ToList();
            var taken = list.Where(bound.ContainsKey).Select(id => bound[id]).ToHashSet(StringComparer.Ordinal);
            foreach (var id in list)
            {
                if (bound.TryGetValue(id, out var code)) { target[id] = code; continue; }
                var candidate = $"{fallbackPrefix}{id}";
                for (var i = 2; !taken.Add(candidate); i++) candidate = $"{fallbackPrefix}{id}_{i}";
                target[id] = candidate;
            }
        }
    }

    private async Task<List<AppLedger>> ExportLedgersAsync(
        List<Module> members, Dictionary<long, string> codeOf, AppReportCodec.Names names, List<string> warnings, CancellationToken ct)
    {
        var result = new List<AppLedger>();
        foreach (var l in (await ledgers.ListAsync(ct)).OrderBy(l => l.ModuleId))
        {
            if (members.FirstOrDefault(m => m.Id == l.ModuleId) is not { } module) continue;   // 不屬於這個應用程式
            var (ledger, errors) = AppEffectsCodec.ToPackage(l, codeOf[module.Id], names);
            if (ledger is null) warnings.Add($"模組「{codeOf[module.Id]}」的分錄設定未匯出（{string.Join("；", errors)}）");
            else result.Add(ledger);
        }
        return result;
    }

    private async Task<List<AppEvent>> ExportEventsAsync(
        List<Module> members, Dictionary<string, string> keyToCode, AppReportCodec.Names names, List<string> warnings,
        HashSet<string> excluded, CancellationToken ct)
    {
        var result = new List<AppEvent>();
        foreach (var e in (await events.ListAsync(ct)).OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (excluded.Contains(e.Key)) continue;
            if (members.FirstOrDefault(m => m.Id == e.SourceModuleId) is not { } source) continue;
            var (package, errors) = AppEffectsCodec.ToPackage(e, source, key => keyToCode.GetValueOrDefault(key), names);
            if (package is null) warnings.Add($"連動事件「{e.Key}」未匯出（{string.Join("；", errors)}）");
            else result.Add(package);
        }
        // 沖銷效果指向的事件沒匯出時，套件重新匯入會被拒：連同它一起不匯出，直到穩定
        for (bool changed = true; changed;)
        {
            changed = false;
            var codes = result.Select(e => e.Code).ToHashSet();
            foreach (var e in result.Where(e => e.Effects.Any(x => x.OfEvent is { } of && !codes.Contains(of))).ToList())
            {
                warnings.Add($"連動事件「{e.Code}」沖銷的事件未匯出，這個事件也未匯出");
                result.Remove(e);
                changed = true;
            }
        }
        return result;
    }

    private async Task<List<AppCheck>> ExportChecksAsync(
        List<Module> members, Dictionary<long, string> codeOf, AppReportCodec.Names names, List<string> warnings,
        HashSet<string> excluded, CancellationToken ct)
    {
        var result = new List<AppCheck>();
        foreach (var c in (await checks.ListAsync(ct)).OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            if (excluded.Contains(c.Key)) continue;
            if (members.FirstOrDefault(m => m.Id == c.ModuleId) is not { } module) continue;
            var (package, errors) = AppEffectsCodec.ToPackage(c, codeOf[module.Id], names);
            if (package is null) warnings.Add($"跨記錄檢查「{c.Key}」未匯出（{string.Join("；", errors)}）");
            else result.Add(package);
        }
        return result;
    }

    private async Task<List<AppPrintTemplate>> ExportPrintTemplatesAsync(
        List<Module> members, Dictionary<long, string> codeOfModule, Dictionary<string, string> keyToCode,
        AppReportCodec.Names names, List<string> warnings, CancellationToken ct)
    {
        var result = new List<AppPrintTemplate>();
        foreach (var m in members.Where(m => !DynamicModules.Domain.Modules.ModuleRelations.IsDetail(m)))
        {
            var module = codeOfModule[m.Id];
            foreach (var t in await printTemplates.ListByModuleAsync(m.Id, ct))
            {
                var converted = AppPrintCodec.ToPackage(t.Body, module, key => keyToCode.GetValueOrDefault(key), names.Field);
                if (converted.Body is null)
                {
                    warnings.Add($"列印範本「{module}.{t.Key}」引用了套件外的明細模組，未匯出");
                    continue;
                }
                foreach (var field in converted.Dropped.Distinct())
                    warnings.Add($"列印範本「{module}.{t.Key}」引用了停用或未匯出的欄位 {field}，匯出時已拿掉");
                result.Add(new AppPrintTemplate(module, t.Key, t.NameI18n, t.Active, converted.Body));
            }
        }
        return result;
    }

    private static List<AppWorkflow> ExportWorkflows(
        List<Module> members, Dictionary<long, string> codeOf, AppReportCodec.Names names, Func<long, string> groupCode, List<string> warnings)
    {
        var result = new List<AppWorkflow>();
        foreach (var m in members.Where(m => m.Workflow is not null))
        {
            var w = m.Workflow!;
            var module = codeOf[m.Id];
            var status = names.Field(w.StatusFieldId);
            string? Option(long id) => names.Option(w.StatusFieldId, id);
            if (status is null || Option(w.InitialOptionId) is not { } initial || w.States.Any(s => Option(s.OptionId) is null)
                || w.Transitions.Any(t => Option(t.From) is null || Option(t.To) is null))
            {
                warnings.Add($"模組「{module}」的狀態機引用了停用或未匯出的欄位、選項，未匯出");
                continue;
            }
            result.Add(new AppWorkflow(module, status[(module.Length + 1)..], initial,
                w.States.Select(s => new AppWorkflowState(Option(s.OptionId)!, s.ReadOnly)).ToList(),
                w.Transitions.Select(t => new AppWorkflowTransition(Option(t.From)!, Option(t.To)!, t.GroupIds.Select(groupCode).ToList())).ToList()));
        }
        return result;
    }

    /// <param name="masterTargets">應用程式用到的主檔模組 Id → 主檔符號：目標是它們的轉換照樣匯出（§4.5），來源仍必須是成員。</param>
    private async Task<List<AppConversion>> ExportConversionsAsync(
        List<Module> members, Dictionary<long, string> codeOf, IReadOnlyDictionary<long, string> masterTargets,
        AppReportCodec.Names names, List<string> warnings, CancellationToken ct)
    {
        var result = new List<AppConversion>();
        var byKey = members.ToDictionary(m => m.Key);
        foreach (var c in (await conversions.ListAsync(ct)).OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            var source = members.FirstOrDefault(m => m.Id == c.SourceModuleId);
            var target = members.FirstOrDefault(m => m.Id == c.TargetModuleId);
            var targetCode = target is not null ? codeOf[target.Id] : masterTargets.GetValueOrDefault(c.TargetModuleId);
            if (source is null || targetCode is null) continue; // 不屬於這個應用程式
            var d = c.Definition;
            var states = d.SourceStates.Select(s => source.Workflow is { } w ? names.Option(w.StatusFieldId, s) : null).ToList();
            AppCopy? Copy(FillMapping x) => names.Field(x.From) is { } from && names.Field(x.To) is { } to ? new AppCopy(from, to) : null;
            var header = names.Field(d.Header.SourceFieldId);
            var headerCopy = d.Header.Copy.Select(Copy).ToList();
            AppConversionLines? lines = null;
            var ok = header is not null && headerCopy.All(x => x is not null) && states.All(s => s is not null);
            if (ok && d.Lines is { } l)
            {
                var sm = byKey.GetValueOrDefault(l.SourceModuleKey);
                var tm = byKey.GetValueOrDefault(l.TargetModuleKey);
                var trace = names.Field(l.SourceLineFieldId);
                var qty = Copy(l.Quantity);
                var copies = l.Copy.Select(Copy).ToList();
                if (sm is null || tm is null || trace is null || qty is null || copies.Any(x => x is null)) ok = false;
                else lines = new AppConversionLines(codeOf[sm.Id], codeOf[tm.Id], trace, qty, copies.OfType<AppCopy>().ToList());
            }
            if (!ok)
            {
                warnings.Add($"轉換「{c.Key}」引用了停用或未匯出的欄位，未匯出");
                continue;
            }
            result.Add(new AppConversion(c.Key, c.NameI18n, c.Active, codeOf[source.Id], targetCode,
                states.OfType<string>().ToList(), new AppConversionHeader(header!, headerCopy.OfType<AppCopy>().ToList()), lines));
        }
        return result;
    }

    private async Task<List<AppPackageReport>> ExportReportsAsync(
        App app, AppReportCodec.Names names, Func<long, string> groupCode, List<string> warnings, CancellationToken ct)
    {
        var result = new List<AppPackageReport>();
        foreach (var link in app.Reports.OrderBy(r => r.Code, StringComparer.Ordinal))
        {
            if (await reports.GetAsync(link.ReportId, ct) is not { } report) continue;
            ReportDefinition definition;
            try { definition = ReportDefinition.Parse(report.DefinitionJson); }
            catch (System.Text.Json.JsonException)
            {
                warnings.Add($"報表「{report.Name}」的定義無法讀取，未匯出");
                continue;
            }
            var (converted, errors) = AppReportCodec.ToPackage(definition, names);
            if (converted is null)
            {
                warnings.Add($"報表「{report.Name}」未匯出（{string.Join("；", errors)}）");
                continue;
            }
            result.Add(new AppPackageReport(link.Code, report.Name, report.Shares.Select(s => groupCode(s.GroupId)).ToList(), converted));
        }
        return result;
    }
}
