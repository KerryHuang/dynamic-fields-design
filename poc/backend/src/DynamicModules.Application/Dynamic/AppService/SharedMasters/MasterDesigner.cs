using System.Security.Cryptography;
using DynamicModules.Application.Common;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.AppService.SharedMasters;

/// <summary>
/// 設計器在共用主檔模組上新增的欄位、選項、區塊，歸屬照所在區塊決定（設計 §5.4）：貢獻者區塊裡的歸貢獻者（對照列含 /、限定可見、
/// 受生命週期鎖管制），基本區塊（含客戶新建的區塊）裡的歸主檔。建立時就寫對照列，代號是 u 加 8 碼小寫英數，之後不隨拖曳改變：
/// 跨歸屬移動 409；孤兒區塊不能新增、拖入，孤兒欄位不能加選項、拖出。不是主檔模組時什麼都不做。
/// </summary>
public sealed class MasterDesigner(IAppRepository apps, ISharedMasterRepository shared, MasterLifecycle lifecycle, IUnitOfWork uow)
{
    public const string OrphanBlockMessage = "這個區塊屬於已刪除的應用程式，不能新增或拖入欄位；請改加在主檔的基本區塊";
    public const string OrphanFieldMessage = "這個欄位屬於已刪除的應用程式，不能加選項或移到別的區塊；請改加在主檔的基本區塊";
    public const string CrossOwnerMessage = "欄位和目標區塊屬於不同的擁有者（共用主檔或不同的應用程式），不能跨區塊移動";

    private const string CodeChars = "abcdefghijklmnopqrstuvwxyz0123456789";

    private sealed record Host(App Master, string MemberCode);

    private async Task<Host?> HostAsync(Module module, CancellationToken ct) =>
        await apps.FindByModuleIdAsync(module.Id, ct) is { Kind: AppKind.Master } master
            ? new Host(master, master.Modules.First(m => m.ModuleId == module.Id).Code)
            : null;

    /// <summary>欄位的擁有者；沒有任何對照列時看所在區塊。</summary>
    private async Task<ItemOwner> OwnerOfFieldAsync(Field field, CancellationToken ct)
    {
        var owner = await shared.FieldOwnerAsync(field.Id, ct);
        return owner.Known ? owner : await shared.BlockOwnerAsync(field.BlockId, ct);
    }

    public async Task<ServiceResult?> RejectAddFieldAsync(Module module, long blockId, CancellationToken ct)
    {
        if (await HostAsync(module, ct) is null) return null;
        return (await shared.BlockOwnerAsync(blockId, ct)).Orphan ? ServiceResult.Conflict(OrphanBlockMessage) : null;
    }

    public async Task<ServiceResult?> RejectAddOptionAsync(Module module, Field field, CancellationToken ct)
    {
        if (await HostAsync(module, ct) is null) return null;
        if ((await shared.BlockOwnerAsync(field.BlockId, ct)).Orphan) return ServiceResult.Conflict(OrphanBlockMessage);
        return (await OwnerOfFieldAsync(field, ct)).Orphan ? ServiceResult.Conflict(OrphanFieldMessage) : null;
    }

    /// <summary>欄位換區塊：目標是孤兒區塊、欄位是孤兒、或兩邊擁有者不同，都回 409。</summary>
    public async Task<ServiceResult?> RejectMoveAsync(Module module, Field field, long newBlockId, CancellationToken ct)
    {
        if (field.BlockId == newBlockId || await HostAsync(module, ct) is null) return null;
        var target = await shared.BlockOwnerAsync(newBlockId, ct);
        if (target.Orphan) return ServiceResult.Conflict(OrphanBlockMessage);
        var owner = await OwnerOfFieldAsync(field, ct);
        if (owner.Orphan) return ServiceResult.Conflict(OrphanFieldMessage);
        return owner.Contributor != target.Contributor ? ServiceResult.Conflict(CrossOwnerMessage) : null;
    }

    /// <summary>新增的欄位寫歸屬：貢獻者區塊裡的標限定可見，替貢獻者在這個模組有權限來源的群組寫可見列。會提交。</summary>
    public async Task BindFieldAsync(Module module, Field field, CancellationToken ct)
    {
        if (await HostAsync(module, ct) is not { } host) return;
        var owner = await shared.BlockOwnerAsync(field.BlockId, ct);
        if (owner.Contributor is { } appId)
        {
            var name = await NewNameAsync(appId, $"{host.Master.Key}/{host.MemberCode}.", b => b.Fields.Keys, ct);
            apps.AddBindings([new AppField { AppId = appId, Name = name, FieldId = field.Id }], [], []);
            field.VisibilityRestricted = true;
            var groups = (await shared.ListGrantsAsync(module.Id, ct))
                .Where(g => g.SourceKind == GrantSource.App && g.AppId == appId).Select(g => g.GroupId).Distinct();
            foreach (var group in groups)
                shared.AddFieldGrant(new FieldGrant { SourceKind = GrantSource.App, FieldId = field.Id, GroupId = group, AppId = appId });
        }
        else
        {
            var name = await NewNameAsync(host.Master.Id, $"{host.MemberCode}.", b => b.Fields.Keys, ct);
            apps.AddBindings([new AppField { AppId = host.Master.Id, Name = name, FieldId = field.Id }], [], []);
        }
        await uow.CommitAsync(ct);
    }

    /// <summary>新增的選項跟著欄位的歸屬。會提交。</summary>
    public async Task BindOptionAsync(Module module, Field field, FieldOption option, CancellationToken ct)
    {
        if (await HostAsync(module, ct) is not { } host) return;
        var owner = await OwnerOfFieldAsync(field, ct);
        var (appId, prefix) = owner.Contributor is { } c
            ? (c, $"{host.Master.Key}/{host.MemberCode}.")
            : (host.Master.Id, $"{host.MemberCode}.");
        var name = await NewNameAsync(appId, prefix, b => b.Options.Keys, ct);
        apps.AddBindings([], [new AppOption { AppId = appId, Name = name, OptionId = option.Id }], []);
        await uow.CommitAsync(ct);
    }

    /// <summary>設計器新建的區塊一律是基本區塊（歸主檔），並維持「基本區塊在前、貢獻區塊在後」的順序。會提交。</summary>
    public async Task BindBlockAsync(Module module, Block block, CancellationToken ct)
    {
        if (await HostAsync(module, ct) is not { } host) return;
        var name = await NewNameAsync(host.Master.Id, $"{host.MemberCode}.", b => b.Blocks.Keys, ct);
        apps.AddBindings([], [], [new AppBlock { AppId = host.Master.Id, Name = name, BlockId = block.Id }]);
        await uow.CommitAsync(ct);
        await lifecycle.ResequenceBlocksAsync(module.Id, ct);
        await uow.CommitAsync(ct);
    }

    /// <summary>持久代號 u＋8 碼亂碼，與資料庫 Id 無關；萬一撞到同一個應用程式既有的對照名稱就重抽。</summary>
    private async Task<string> NewNameAsync(long appId, string prefix, Func<AppBindings, IEnumerable<string>> names, CancellationToken ct)
    {
        var taken = names(await apps.GetBindingsAsync(appId, ct)).ToHashSet(StringComparer.Ordinal);
        while (true)
        {
            var name = prefix + NewCode();
            if (!taken.Contains(name)) return name;
        }
    }

    public static string NewCode() => "u" + RandomNumberGenerator.GetString(CodeChars, 8);
}
