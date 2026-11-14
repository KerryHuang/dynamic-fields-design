using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.PrintService;

/// <summary>設計器：列出模組的所有範本（含停用與內文）。</summary>
public record GetPrintTemplateDefinitionsRequest(string ModuleKey) : IRequest<ServiceResult>, IDesignerRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}

public record CreatePrintTemplateRequest(string ModuleKey, PrintTemplateInput Input) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ModuleKey: ModuleKey)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

/// <summary>更新時代碼不能改。</summary>
public record UpdatePrintTemplateRequest(string ModuleKey, string Key, PrintTemplateInput Input) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ModuleKey: ModuleKey)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

public record DeletePrintTemplateRequest(string ModuleKey, string Key) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ModuleKey: ModuleKey)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

/// <summary>
/// 某個模組目前使用者可以選的列印範本（啟用的；需要該模組的列印／匯出權限）。
/// 帶 RecordId 時，一般權限不成立的簽核人可用臨時讀取（簽核設計 §10.1）列出這一筆的範本。
/// </summary>
public record GetPrintTemplatesRequest(string ModuleKey, long? RecordId = null) : IRequest<ServiceResult>;

/// <summary>算出一筆記錄的列印結果（完整 HTML 文件）。</summary>
public record PrintRecordRequest(long RecordId, string TemplateKey, string? Locale) : IRequest<ServiceResult>;
