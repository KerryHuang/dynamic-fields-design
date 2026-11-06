using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.AccountService.Commands.CreateGroup;
using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Application.Dynamic.TemplateService.Commands.InstantiateTemplate;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.AppService.Commands;

/// <summary>開啟、關閉、重新開啟、刪除都要設計器對應動作軸加系統管理員：開啟會替群組授權，不該再放大「設計器權限能替自己提權」的風險。</summary>
public record OpenAppRequest(string PackageId, OpenAppBody Body)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, ISystemAdminRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Add;
}

public class OpenAppHandler(
    AppOpenPlanner planner, TemplateInstantiator instantiator, ISender sender, IAppRepository apps,
    AppResponseBuilder builder, IUnitOfWork uow, ICurrentUser user, IBusinessTime time) : IRequestHandler<OpenAppRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(OpenAppRequest request, CancellationToken ct)
    {
        var plan = await planner.PlanAsync(request.PackageId, request.Body, ct);
        if (plan.Failure is { } failure) return failure;
        var preview = plan.Preview;
        if (preview.Errors.Count > 0)
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["package"] = [.. preview.Errors] });
        if (!preview.CanOpen) return ServiceResult.ConflictWith("無法開啟應用程式", preview);

        var package = plan.Source!.Package;
        var body = request.Body;
        var warnings = new List<string>(preview.Warnings);

        // 群組：map 用既有的、create 建新的（Key 由 CreateGroup 自動產生）、skip 與未列出的一律不給權限
        var groupIds = new Dictionary<string, long>(plan.MappedGroups);
        foreach (var (code, entry) in body.GroupMap.Where(kv => kv.Value.Mode == "create"))
        {
            var name = package.Groups.First(g => g.Code == code).Name.Resolve(body.Locale);
            var created = await sender.Send(new CreateGroupRequest(name, body.Locale), ct);
            if (!created.IsSuccess) return created;
            groupIds[code] = ((AdminGroupResponse)created.Data!).Id;
        }

        var overrides = package.Modules.ToDictionary(m => m.Code,
            m => new InstantiateModuleInput(m.Code, plan.FinalKeys[m.Code], null));
        var instantiated = await instantiator.InstantiateAsync(AppPackageAdapter.ToTemplate(package), overrides,
            body.ExcludeFields.ToHashSet(), ct,
            new AppInstantiation(groupIds, plan.ExternalModuleKeys, package.FieldAccess, package.OptionScopes));
        if (!instantiated.IsSuccess) return instantiated;
        var result = (InstantiateTemplateResponse)instantiated.Data!;
        warnings.AddRange(result.Warnings);

        // 啟用：被參照的先啟用。套件的模組開啟後直接是 Active，沒有開了但還是草稿的中間狀態；
        // 啟用必須排在建立檢視之後（建立檢視在實例化的步驟 9）
        foreach (var code in ActivationOrder(package))
        {
            var module = result.Modules.First(m => m.Code == code);
            var activated = await sender.Send(new ActivateModuleRequest(module.Key), ct);
            if (!activated.IsSuccess) return activated;
        }

        var app = new App
        {
            Key = package.Manifest.Id, NameI18n = package.Manifest.Name, Icon = package.Manifest.Icon,
            PackageVersion = package.Manifest.Version, FormatVersion = package.FormatVersion, Source = plan.Source.Source,
            Status = AppStatus.Open, Requires = AppOpenPlanner.RequiresOf(package),
            Modules = result.Modules.Select(m => new AppModule { Code = m.Code, ModuleId = m.Id }).ToList()
        };
        app.TouchRowVersion();
        app.StampCreated(user.UserId, time.Now);
        apps.Add(app);
        await uow.CommitAsync(ct);
        return ServiceResult.Created(await builder.BuildAsync(app, withDependents: false, warnings, ct));
    }

    /// <summary>模組代號依「被本套件內其他模組參照的先啟用」排序；循環時照宣告順序。</summary>
    internal static IReadOnlyList<string> ActivationOrder(AppPackageData package)
    {
        var codes = package.Modules.Select(m => m.Code).ToList();
        var targets = package.Modules.ToDictionary(m => m.Code, m => m.Blocks.SelectMany(b => b.Fields)
            .Where(f => f.Type == FieldType.Reference)
            .Select(f => TemplateGraph.ConfigObject(f.Config, "refTarget"))
            .Where(t => t is not null && TemplateGraph.ConfigString(t.Value, "app") is null
                && TemplateGraph.ConfigString(t.Value, "kind") == "module")
            .Select(t => TemplateGraph.ConfigString(t!.Value, "key")!)
            .Where(k => codes.Contains(k) && k != m.Code).Distinct().ToList());
        var order = new List<string>();
        var visiting = new HashSet<string>();
        void Visit(string code)
        {
            if (order.Contains(code) || !visiting.Add(code)) return;
            foreach (var target in targets[code]) Visit(target);
            order.Add(code);
        }
        foreach (var code in codes) Visit(code);
        return order;
    }
}
