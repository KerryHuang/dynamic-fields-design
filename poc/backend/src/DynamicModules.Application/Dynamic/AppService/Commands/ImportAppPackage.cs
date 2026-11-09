using System.Text;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.AppService.SharedMasters;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.AppService.Commands;

/// <summary>Content 是套件檔的位元組（v1 範本檔或 v2 套件檔）。</summary>
public record ImportAppPackageRequest(byte[] Content) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, ISystemAdminRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Add;
}

/// <summary>
/// 新的套件存進 Dynamic_AppPackage；id 已存在時一律 409，只有共用主檔例外：再匯入主檔是合併（共用主檔設計 §9），
/// 還沒開啟的只合併定義，已開啟或關閉的再依持久代號補進資料庫（只補不刪不覆寫），回 200 與 warnings。
/// </summary>
public class ImportAppPackageHandler(
    AppCatalog catalog, AppPackageSource packages, IAppPackageRepository repository, IAppRepository apps,
    IUnitOfWork uow, ICurrentUser user, IBusinessTime time, MasterMerger merger) : IRequestHandler<ImportAppPackageRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ImportAppPackageRequest request, CancellationToken ct)
    {
        var parsed = AppJson.Parse(request.Content);
        var (package, readErrors) = await packages.ReadAsync(parsed, ct);
        if (package is null) return Invalid(readErrors);

        // 已知的主檔（內建與匯入）：requires.apps 不得列主檔，引用的主檔欄位要找得到（§4.6）；不在的主檔留到開啟時回 masterNotAvailable
        var known = await packages.ListAsync(ct);
        var masters = known.Where(k => k.Package.Manifest.IsMaster)
            .GroupBy(k => k.Package.Manifest.Id).ToDictionary(g => g.Key, g => g.First().Package);
        var validation = AppPackageValidator.Validate(package, masters);
        if (validation.Errors.Count > 0) return Invalid(validation.Errors);

        var id = package.Manifest.Id;
        var stored = await repository.GetByKeyAsync(id, ct);
        var exists = catalog.BuiltIns.Any(p => p.Manifest.Id == id) || stored is not null;
        SourcedPackage? current = null;
        if (exists)
        {
            current = await packages.FindAsync(id, ct);
            var app = await apps.GetByKeyAsync(id, ct);
            if (!package.Manifest.IsMaster || current is null || !current.Package.Manifest.IsMaster || app is { Kind: not AppKind.Master })
                return ServiceResult.Conflict("套件代碼已存在");
        }

        // requires（含 requires.masters）不得循環：把新套件加進「所有已知套件加上已開啟應用程式的 requires」的圖
        var edges = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var k in known)
            edges[k.Package.Manifest.Id] = AppDependencyGraph.RequiresOf(k.Package.Manifest.Requires);
        foreach (var app in await apps.ListAsync(ct)) edges[app.Key] = AppDependencyGraph.RequiresOf(app.Requires);
        edges[id] = AppDependencyGraph.RequiresOf(package.Manifest.Requires);
        if (AppDependencyGraph.FindCycle(edges.Select(kv => (kv.Key, kv.Value))) is { } cycle)
            return Invalid([$"requires 有循環：{string.Join(" → ", cycle)}"]);

        if (current is not null) return await MergeAsync(package, current.Package, stored, masters, ct);

        var c = request.Content;
        var skip = c.Length >= 3 && c[0] == 0xEF && c[1] == 0xBB && c[2] == 0xBF ? 3 : 0;
        var row = new AppPackage { Key = id, Version = package.Manifest.Version, Json = Encoding.UTF8.GetString(c, skip, c.Length - skip) };
        row.StampCreated(user.UserId, time.Now);
        repository.Add(row);
        await uow.CommitAsync(ct);
        return ServiceResult.Created(AppResponseBuilder.Summarize(new SourcedPackage(package, AppPackageSource.Imported), null));
    }

    /// <summary>主檔合併：先合併定義並驗證，資料庫裡已有主檔就依持久代號補進去，最後把匯入的那份更新成合併結果。</summary>
    private async Task<ServiceResult> MergeAsync(
        AppPackageData incoming, AppPackageData current, AppPackage? stored, IReadOnlyDictionary<string, AppPackageData> masters, CancellationToken ct)
    {
        var (merged, warnings) = MasterMerger.MergeDefinition(current, incoming);
        var others = masters.Where(m => m.Key != merged.Manifest.Id).ToDictionary(m => m.Key, m => m.Value);
        var validation = AppPackageValidator.Validate(merged, others);
        if (validation.Errors.Count > 0) return Invalid(validation.Errors.Select(e => $"合併後的定義：{e}"));

        var app = await apps.GetByKeyAsync(merged.Manifest.Id, ct);
        if (app is not null)
        {
            var dbWarnings = new List<string>();
            if (await merger.MergeIntoDatabaseAsync(app, incoming, dbWarnings, ct) is { } failed) return failed;
            // 資料庫層的警告（實際補進去的結果）比定義層準；定義層只多出「沒有合併」的那幾類
            warnings = [.. dbWarnings, .. warnings.Where(w => w.Contains("沒有合併") && !dbWarnings.Contains(w))];
        }

        var json = JsonSerializer.Serialize(merged, TemplateJson.Options);
        if (stored is null)
        {
            stored = new AppPackage { Key = merged.Manifest.Id, Version = merged.Manifest.Version, Json = json };
            stored.StampCreated(user.UserId, time.Now);
            repository.Add(stored);
        }
        else
        {
            stored.Json = json;
            stored.Version = merged.Manifest.Version;
            stored.StampModified(user.UserId, time.Now);
            repository.Update(stored);
        }
        await uow.CommitAsync(ct);
        var status = app is null ? null : AppMapper.StatusName(app.Status);
        return ServiceResult.Success(AppResponseBuilder.Summarize(new SourcedPackage(merged, AppPackageSource.Imported), status)
            with { Merged = true, Warnings = warnings });
    }

    private static ServiceResult Invalid(IEnumerable<string> errors) =>
        ServiceResult.Invalid(new Dictionary<string, string[]> { ["package"] = [.. errors] });
}
