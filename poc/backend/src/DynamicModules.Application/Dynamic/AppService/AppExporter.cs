using System.Text.RegularExpressions;
using DynamicModules.Application.Dynamic.AppService.Data;
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
    IConversionRepository conversions, IReportRepository reports, IPrintTemplateRepository printTemplates)
{
    [GeneratedRegex("[^a-zA-Z0-9_]")]
    private static partial Regex NotCode();

    public sealed record Exported(AppPackageData Package, List<string> Warnings);

    public async Task<Exported> ExportAsync(App app, CancellationToken ct)
    {
        var all = await modules.ListAsync(ct);
        var members = new List<Module>();
        foreach (var member in app.Modules.OrderBy(m => m.Code, StringComparer.Ordinal))
            if (await modules.GetByIdAsync(member.ModuleId, includeChildren: true, ct) is { } m) members.Add(m);
        var codeOfModule = app.Modules.ToDictionary(m => m.ModuleId, m => m.Code);
        var keyToCode = members.ToDictionary(m => m.Key, m => codeOfModule[m.Id]);

        // 別的應用程式的模組：key → (模組代號, 應用程式 id)
        var moduleKeyById = all.ToDictionary(m => m.Id, m => m.Key);
        var external = new Dictionary<string, (string Code, string App)>();
        foreach (var other in (await apps.ListAsync(ct)).Where(a => a.Id != app.Id))
            foreach (var m in other.Modules.Where(m => moduleKeyById.ContainsKey(m.ModuleId)))
                external[moduleKeyById[m.ModuleId]] = (m.Code, other.Key);
        var usedApps = new Dictionary<string, HashSet<string>>();
        (string Code, string? App)? RefTarget(string key)
        {
            if (keyToCode.TryGetValue(key, out var code)) return (code, null);
            if (!external.TryGetValue(key, out var ext)) return null;
            if (!usedApps.TryGetValue(ext.App, out var set)) usedApps[ext.App] = set = [];
            set.Add(ext.Code);
            return (ext.Code, ext.App);
        }

        var exported = await templates.ExportMembersAsync(members, m => codeOfModule[m.Id], RefTarget, ct);
        var warnings = new List<string>(exported.Warnings);
        var names = exported.Names;

        // 群組：Key 當代號（自動產生的 g-xxxx 含連字號，換成合法代號）；用到才輸出
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

        // 模組權限裡的群組 Key 換成代號
        var moduleList = exported.Modules.Select(m => m with
        {
            Permissions = m.Permissions.Where(p => groupIdOfKey.ContainsKey(p.Group))
                .Select(p => p with { Group = GroupCode(groupIdOfKey[p.Group]) }).ToList()
        }).ToList();

        var fieldAccess = new List<AppFieldAccess>();
        var optionScopes = new List<AppOptionScope>();
        foreach (var m in members)
        {
            var rows = await permissions.LoadModuleRowsAsync(m, ct);
            foreach (var p in rows.FieldPermissions)
                if (names.Field(p.FieldId) is { } field && groupList.Any(g => g.Id == p.GroupId))
                    fieldAccess.Add(new AppFieldAccess(field, GroupCode(p.GroupId), p.Access));
            foreach (var scope in rows.OptionScopes.GroupBy(o => o.OptionId))
            {
                var module = codeOfModule[m.Id];
                // 選項所屬欄位必須實際匯出（沒停用、沒被省略），否則 optionScopes 會指到檔案裡不存在的選項，重新匯入時被拒
                var owner = m.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Options.Any(o => o.Id == scope.Key && o.Active));
                var option = owner?.Options.First(o => o.Id == scope.Key);
                if (owner is null || option is null || names.Option(owner.Id, option.Id) is null || names.Field(owner.Id) is null) continue;
                optionScopes.Add(new AppOptionScope($"{module}.o_{option.Id}",
                    scope.Select(s => s.GroupId).Distinct().Where(id => groupList.Any(g => g.Id == id)).Select(GroupCode).ToList()));
            }
        }

        var workflows = ExportWorkflows(members, codeOfModule, names, GroupCode, warnings);
        var conversionList = await ExportConversionsAsync(members, codeOfModule, names, warnings, ct);
        var reportList = await ExportReportsAsync(app, names, GroupCode, warnings, ct);
        var printList = await ExportPrintTemplatesAsync(members, codeOfModule, keyToCode, names, warnings, ct);

        var entities = exported.Modules.Where(m => m.HostEntityKey is not null).Select(m => m.HostEntityKey!)
            .Concat(exported.Modules.SelectMany(m => m.Blocks).SelectMany(b => b.Fields)
                .Where(f => f.Type == FieldType.Reference)
                .Select(f => TemplateGraph.ConfigObject(f.Config, "refTarget"))
                .Where(t => t is not null && TemplateGraph.ConfigString(t.Value, "kind") == ReferenceTarget.EntityKind)
                .Select(t => TemplateGraph.ConfigString(t!.Value, "key")!))
            .Distinct().Order(StringComparer.Ordinal).ToList();
        var requires = new AppManifestRequires(
            usedApps.OrderBy(a => a.Key, StringComparer.Ordinal)
                .Select(a => new AppRequirementData(a.Key, a.Value.Order(StringComparer.Ordinal).ToList())).ToList(),
            entities);

        var groups = codeOfGroup.OrderBy(g => g.Key)
            .Select(g => new AppGroup(g.Value, groupList.First(x => x.Id == g.Key).NameI18n)).ToList();
        var package = new AppPackageData(
            AppPackageData.FormatName, AppPackageData.CurrentFormatVersion,
            new AppManifest(app.Key, app.PackageVersion, app.NameI18n, null, app.Icon, null, requires),
            groups, moduleList, fieldAccess, optionScopes, reportList, null, workflows, conversionList, null, printList);
        // groups 在上面各步驟用到才加進 codeOfGroup，最後才組出來
        return new Exported(package, warnings);
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

    private async Task<List<AppConversion>> ExportConversionsAsync(
        List<Module> members, Dictionary<long, string> codeOf, AppReportCodec.Names names, List<string> warnings, CancellationToken ct)
    {
        var result = new List<AppConversion>();
        var byKey = members.ToDictionary(m => m.Key);
        foreach (var c in (await conversions.ListAsync(ct)).OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            var source = members.FirstOrDefault(m => m.Id == c.SourceModuleId);
            var target = members.FirstOrDefault(m => m.Id == c.TargetModuleId);
            if (source is null || target is null) continue; // 不屬於這個應用程式
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
            result.Add(new AppConversion(c.Key, c.NameI18n, c.Active, codeOf[source.Id], codeOf[target.Id],
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
