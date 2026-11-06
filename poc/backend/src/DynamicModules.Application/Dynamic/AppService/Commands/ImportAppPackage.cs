using System.Text;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.AppService.Commands;

/// <summary>Content 是套件檔的位元組（v1 範本檔或 v2 套件檔）。</summary>
public record ImportAppPackageRequest(byte[] Content) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, ISystemAdminRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Add;
}

public class ImportAppPackageHandler(
    AppCatalog catalog, AppPackageSource packages, IAppPackageRepository repository, IAppRepository apps,
    IUnitOfWork uow, ICurrentUser user, IBusinessTime time) : IRequestHandler<ImportAppPackageRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ImportAppPackageRequest request, CancellationToken ct)
    {
        var parsed = AppJson.Parse(request.Content);
        var (package, readErrors) = await packages.ReadAsync(parsed, ct);
        if (package is null) return Invalid(readErrors);

        var validation = AppPackageValidator.Validate(package);
        if (validation.Errors.Count > 0) return Invalid(validation.Errors);

        var id = package.Manifest.Id;
        if (catalog.BuiltIns.Any(p => p.Manifest.Id == id) || await repository.GetByKeyAsync(id, ct) is not null)
            return ServiceResult.Conflict("套件代碼已存在");

        // requires 不得循環：把新套件加進「所有已知套件加上已開啟應用程式的 requires」的圖
        var edges = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var known in await packages.ListAsync(ct))
            edges[known.Package.Manifest.Id] = known.Package.Manifest.Requires.Apps.Select(a => a.Id).ToList();
        foreach (var app in await apps.ListAsync(ct)) edges[app.Key] = app.Requires.Apps.Select(a => a.Id).ToList();
        edges[id] = package.Manifest.Requires.Apps.Select(a => a.Id).ToList();
        if (AppDependencyGraph.FindCycle(edges.Select(kv => (kv.Key, kv.Value))) is { } cycle)
            return Invalid([$"requires 有循環：{string.Join(" → ", cycle)}"]);

        var c = request.Content;
        var skip = c.Length >= 3 && c[0] == 0xEF && c[1] == 0xBB && c[2] == 0xBF ? 3 : 0;
        var row = new AppPackage { Key = id, Version = package.Manifest.Version, Json = Encoding.UTF8.GetString(c, skip, c.Length - skip) };
        row.StampCreated(user.UserId, time.Now);
        repository.Add(row);
        await uow.CommitAsync(ct);
        return ServiceResult.Created(AppResponseBuilder.Summarize(new SourcedPackage(package, AppPackageSource.Imported), null));
    }

    private static ServiceResult Invalid(IEnumerable<string> errors) =>
        ServiceResult.Invalid(new Dictionary<string, string[]> { ["package"] = [.. errors] });
}
