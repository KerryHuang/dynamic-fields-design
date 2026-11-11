using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Rules;
using MediatR;

namespace DynamicModules.Application.Dynamic.AttachmentService;

public sealed class GetAttachmentSettingsHandler(AttachmentSettings settings, IPolicyReader policies)
    : IRequestHandler<GetAttachmentSettingsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetAttachmentSettingsRequest request, CancellationToken ct) =>
        ServiceResult.Success(new AttachmentSettingsResponse(
            settings.UploadEnabled, settings.UploadEnabled ? null : settings.DisabledReason,
            await policies.IntAsync(PolicyCatalog.AttachmentMaxFileBytes, null, ct),
            await policies.ListAsync(PolicyCatalog.AttachmentAllowedExtensions, null, ct),
            await policies.IntAsync(PolicyCatalog.AttachmentMaxFilesPerField, null, ct)));
}

/// <summary>上傳前的資格檢查結果：通過時帶著後面要用的記錄、模組、欄位與目前的值。</summary>
public sealed record UploadTarget(
    Record Record, Module Module, Field Field, Dictionary<string, JsonElement?> Previous);

/// <summary>
/// 上傳資格：權限、資料範圍、欄位可寫（含規則唯讀與狀態機唯讀狀態）、Closed 模組、環境開關。
/// 不讀請求主體，所以控制器可以在 model binding 收進檔案之前先跑一次；實際上傳時處理器會再跑一次（以最新資料為準）。
/// </summary>
public sealed class AttachmentUploadGate(
    IModuleRepository modules, IRecordRepository records, IDynamicAccessGuard guard, FieldTypeRegistry registry,
    AttachmentSettings settings, IBusinessTime time)
{
    public async Task<(ServiceResult? Rejected, UploadTarget? Target)> CheckAsync(long recordId, long fieldId, CancellationToken ct)
    {
        var record = await records.GetAsync(recordId, ct);
        if (record is null) return (ServiceResult.NotFound(RecordMapper.RecordNotFound), null);
        var module = await modules.GetByIdAsync(record.ModuleId, includeChildren: true, ct);
        if (module is null || ModuleRelations.IsDetail(module)) return (ServiceResult.NotFound(RecordMapper.RecordNotFound), null);
        if (await ModuleGate.RejectUnwritableAsync(module, guard, ct) is { } blocked) return (blocked, null);
        var access = await guard.AuthorizeAsync(module, PermissionAction.Edit, ct);
        if (access.Permissions is not { } perms) return (access.Denied!, null);
        if (perms.OwnerUserIds is not null && !perms.OwnerUserIds.Contains(record.OwnerUserId))
            return (ServiceResult.NotFound(RecordMapper.RecordNotFound), null);

        var field = module.Blocks.SelectMany(b => b.Fields)
            .FirstOrDefault(f => f.Id == fieldId && f.Status == FieldStatus.Active && f.Type == FieldType.Attachment);
        if (field is null || !perms.CanSee(field)) return (ServiceResult.NotFound(MessageRef.Of(MessageText.AttachmentFieldNotFound)), null);

        var userIds = await modules.ListUserIdsAsync(ct);
        var previous = RecordValueProcessor.Parse(record.ValuesJson);
        var today = time.Today;
        if (RuleEngine.Evaluate(module, previous, previous, registry, userIds, today).ReadOnlyAll)
            return (Invalid(RecordValueProcessor.RecordErrorKey, MessageRef.Of(MessageText.RecordReadOnly)), null);
        if (module.Workflow is { } wf && wf.IsLocked(previous))
            return (Invalid(RecordValueProcessor.RecordErrorKey, MessageRef.Of(MessageText.RecordReadOnly)), null);
        if (!RecordValueProcessor.WritablePredicate(module, previous, perms, registry, userIds, today)(field))
            return (Invalid(field.Key, "此欄位目前不可修改"), null);

        if (!settings.UploadEnabled) return (UploadDisabled(settings), null);
        return (null, new UploadTarget(record, module, field, previous));
    }

    public static ServiceResult UploadDisabled(AttachmentSettings settings) =>
        ServiceResult.Conflict(settings.DisabledReason is { Length: > 0 } reason
            ? $"{AttachmentRules.UploadDisabledPrefix}：{reason}" : AttachmentRules.UploadDisabledPrefix);

    private static ServiceResult Invalid(string key, string message) =>
        ServiceResult.Invalid(new Dictionary<string, string[]> { [key] = [message] });

    private static ServiceResult Invalid(string key, MessageRef message) => ServiceResult.Invalid(key, message);
}

/// <summary>
/// 控制器在收進請求主體之前先問：這個人現在能不能對這個欄位上傳？只做檢查，不寫入。
/// 通過時回該模組的 <see cref="UploadBodyLimit"/>（主體上限要看模組，所以必須先知道記錄屬於哪個模組）。
/// </summary>
public sealed class CheckAttachmentUploadHandler(AttachmentUploadGate gate, IPolicyReader policies)
    : IRequestHandler<CheckAttachmentUploadRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CheckAttachmentUploadRequest request, CancellationToken ct)
    {
        var (rejected, target) = await gate.CheckAsync(request.RecordId, request.FieldId, ct);
        if (rejected is not null) return rejected;
        return ServiceResult.Success(new UploadBodyLimit(
            await policies.IntAsync(PolicyCatalog.AttachmentMaxFileBytes, target!.Module.Id, ct), 1));
    }
}

/// <summary>
/// 上傳：先過資格檢查（<see cref="AttachmentUploadGate"/>），再驗檔名、副檔名白名單、大小與數量，
/// 最後把檔案描述寫進記錄的值（走歷程與 RowVersion）。檔案先寫、值後寫：值寫入失敗時刪除剛寫的檔案。
/// </summary>
public sealed class UploadAttachmentHandler(
    AttachmentUploadGate gate, IAttachmentStore store, IPolicyReader policies, RecordHistoryWriter history, IUnitOfWork uow,
    ICurrentUser user, IBusinessTime time) : IRequestHandler<UploadAttachmentRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UploadAttachmentRequest request, CancellationToken ct)
    {
        var (rejected, target) = await gate.CheckAsync(request.RecordId, request.FieldId, ct);
        if (rejected is not null) return rejected;
        var (record, module, field, previous) = target!;

        var name = AttachmentRules.CleanName(request.FileName);
        if (name is null) return Invalid(field.Key, AttachmentRules.NameRequired);
        var ext = AttachmentRules.ExtensionOf(name);
        if (ext is null || !await AttachmentRules.IsAllowedAsync(policies, ext, ct)) return Invalid(field.Key, AttachmentRules.ExtensionRejected);

        var existing = AttachmentValue.Parse(previous.GetValueOrDefault(field.Key));
        if (existing.Count >= await policies.IntAsync(PolicyCatalog.AttachmentMaxFilesPerField, module.Id, ct))
            return Invalid(field.Key, AttachmentRules.TooMany);

        var id = Guid.NewGuid().ToString("N");
        var size = await store.SaveAsync(id, request.Content, await policies.IntAsync(PolicyCatalog.AttachmentMaxFileBytes, module.Id, ct), ct);
        if (size is null) return Invalid(field.Key, AttachmentRules.TooLarge);

        try
        {
            var values = new Dictionary<string, JsonElement?>(previous)
            {
                [field.Key] = AttachmentValue.ToJson([.. existing, new AttachmentRef(id, name, size.Value)])
            };
            record.ValuesJson = RecordValueProcessor.Serialize(values);
            history.Updated(record, module, previous, values);
            record.RowVersion = Guid.NewGuid().ToString();
            record.StampModified(user.UserId, time.Now);
            await uow.CommitAsync(ct);
            return ServiceResult.Created(new UploadedAttachment(id, name, size.Value, record.RowVersion));
        }
        catch
        {
            // 檔案先寫、記錄後寫：記錄寫入失敗（含 RowVersion 競爭）時把剛寫的檔案收掉，不留孤兒
            store.Delete(id);
            throw;
        }
    }

    private static ServiceResult Invalid(string key, string message) =>
        ServiceResult.Invalid(new Dictionary<string, string[]> { [key] = [message] });

    private static ServiceResult Invalid(string key, MessageRef message) => ServiceResult.Invalid(key, message);
}

/// <summary>
/// 下載：和讀取記錄同一套檢查（模組可讀、Read 權限、資料範圍、欄位看得到），檔案必須是這筆記錄這個欄位目前的值裡列出的；
/// 軟刪除的記錄、被移除的檔案、看不到的欄位都一律回找不到。Closed 模組可以下載。
/// </summary>
public sealed class DownloadAttachmentHandler(
    IModuleRepository modules, IRecordRepository records, IDynamicAccessGuard guard, IAttachmentStore store,
    ApprovalService.ApprovalReadGrant grant)
    : IRequestHandler<DownloadAttachmentRequest, ServiceResult>
{
    private static readonly MessageRef FileNotFound = MessageRef.Of(MessageText.AttachmentNotFound);

    public async Task<ServiceResult> Handle(DownloadAttachmentRequest request, CancellationToken ct)
    {
        var record = await records.GetAsync(request.RecordId, ct);
        if (record is null) return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        var module = await modules.GetByIdAsync(record.ModuleId, includeChildren: true, ct);
        if (!module.IsReadable() || ModuleRelations.IsDetail(module)) return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        var access = await guard.AuthorizeAsync(module, PermissionAction.Read, ct);
        var perms = access.Permissions;
        if (perms is null || (perms.OwnerUserIds is { } owners && !owners.Contains(record.OwnerUserId)))
        {
            // 簽核人的臨時讀取權（簽核設計 §10.1），規則同單筆讀取
            if (await grant.ForRecordAsync(module, record, ct) is not { } g)
                return perms is null ? access.Denied! : ServiceResult.NotFound(RecordMapper.RecordNotFound);
            perms = g;
        }

        var field = module.Blocks.SelectMany(b => b.Fields)
            .FirstOrDefault(f => f.Id == request.FieldId && f.Status == FieldStatus.Active && f.Type == FieldType.Attachment);
        if (field is null || !perms.CanSee(field)) return ServiceResult.NotFound(FileNotFound);

        var values = RecordValueProcessor.Parse(record.ValuesJson);
        var file = AttachmentValue.Parse(values.GetValueOrDefault(field.Key)).FirstOrDefault(f => f.Id == request.FileId);
        if (file is null) return ServiceResult.NotFound(FileNotFound);
        var stream = store.OpenRead(file.Id);
        return stream is null ? ServiceResult.NotFound(FileNotFound) : ServiceResult.Success(new AttachmentDownload(stream, file.Name, file.Size));
    }
}

public sealed class SweepAttachmentsHandler(IModuleRepository modules, IRecordRepository records, ITaskRepository tasks, IAttachmentStore store)
    : IRequestHandler<SweepAttachmentsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(SweepAttachmentsRequest request, CancellationToken ct)
    {
        // 被參照＝任何記錄（含軟刪除，還原後要用）目前的值裡列出，或是未撤回的任務留言的附件。
        // 永久清除的記錄、撤回的留言、刪掉的任務不在名單內，它們的檔案就成了孤兒。
        var referenced = await tasks.ListLiveAttachmentKeysAsync(ct);
        foreach (var listed in await modules.ListAsync(ct))
        {
            var module = await modules.GetByIdAsync(listed.Id, includeChildren: true, ct);
            var keys = module?.Blocks.SelectMany(b => b.Fields).Where(f => f.Type == FieldType.Attachment).Select(f => f.Key).ToList();
            if (keys is not { Count: > 0 }) continue;
            foreach (var record in await records.ListAllByModuleAsync(listed.Id, ct))
            {
                var values = RecordValueProcessor.Parse(record.ValuesJson);
                foreach (var key in keys)
                    foreach (var f in AttachmentValue.Parse(values.GetValueOrDefault(key))) referenced.Add(f.Id);
            }
        }

        var cutoff = DateTime.UtcNow - request.Grace;
        int deleted = 0, kept = 0;
        foreach (var file in store.List())
        {
            // 寬限時間內的不動：上傳先寫檔、後寫記錄，剛寫的檔案還沒被參照不算孤兒
            if (referenced.Contains(file.Id) || file.LastWriteUtc > cutoff) { kept++; continue; }
            if (store.Delete(file.Id)) deleted++;
        }
        return ServiceResult.Success(new SweepAttachmentsResult(deleted, kept));
    }
}
