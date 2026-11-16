using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.TemplateService.Queries.GetTemplates;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.AppService;

/// <summary>組應用程式與套件的回應；成員模組的現況（key、名稱、狀態）取自目前的 Module，不是套件原文。</summary>
public sealed class AppResponseBuilder(IModuleRepository modules, AppDependencyChecker dependencies, IAppRepository apps)
{
    public async Task<AppResponse> BuildAsync(App app, bool withDependents, IEnumerable<string>? warnings, CancellationToken ct)
    {
        var byId = (await modules.ListAsync(ct)).ToDictionary(m => m.Id);
        var members = app.Modules.OrderBy(m => m.Code, StringComparer.Ordinal)
            .Where(m => byId.ContainsKey(m.ModuleId))
            .Select(m => new AppModuleResponse(m.Code, byId[m.ModuleId].Key, m.ModuleId, byId[m.ModuleId].NameI18n,
                byId[m.ModuleId].Status.ToString())).ToList();
        var dependents = new List<string>();
        var referrers = new List<string>();
        if (withDependents)
        {
            var result = await dependencies.DependentsOfAsync(app, ct, forClose: true);
            dependents = result.Apps.Select(a => a.Key).ToList();
            referrers = [.. result.UnaffiliatedReferrers];
        }
        var integrations = new List<IntegrationResponse>();
        if (app.IntegrationsJson is not null)
        {
            var rows = (await apps.ListIntegrationsAsync(ct)).Where(r => r.AppId == app.Id).ToDictionary(r => r.Code);
            foreach (var d in Integrations.IntegrationDeclarations.Parse(app.IntegrationsJson).Where(d => rows.ContainsKey(d.Code)))
            {
                var row = rows[d.Code];
                integrations.Add(new IntegrationResponse(d.Code, d.Name, row.Enabled, row.Installed, row.Status, row.Reason,
                    d.OnPartnerClosed ?? "skip", d.With.Select(w => new AppRequirementData(w.Id, w.Modules)).ToList()));
            }
        }
        return new AppResponse(app.Key, app.NameI18n, app.Icon, app.PackageVersion, app.FormatVersion, app.Source,
            AppMapper.StatusName(app.Status), app.RowVersion, app.Requires, members, dependents, referrers, [.. warnings ?? []], integrations)
        {
            Kind = app.Kind == AppKind.Master ? AppManifest.MasterKindName : AppManifest.AppKindName
        };
    }

    public static AppPackageSummary Summarize(SourcedPackage source, string? status)
    {
        var p = source.Package;
        var template = GetTemplatesHandler.Summarize(AppPackageAdapter.ToTemplate(p), source.Source);
        return new AppPackageSummary(p.Manifest.Id, p.Manifest.Version, p.Manifest.Name, p.Manifest.Description, p.Manifest.Icon,
            p.Manifest.Vendor, source.Source, p.Manifest.Requires, p.Groups, template.Modules, status);
    }
}
