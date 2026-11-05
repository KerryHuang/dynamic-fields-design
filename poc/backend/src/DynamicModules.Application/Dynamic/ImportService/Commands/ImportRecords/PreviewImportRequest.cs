using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.ImportService.Commands.ImportRecords;

/// <summary>匯入預覽（commit=false）：解析並驗證整個檔案，不寫入任何東西。</summary>
public record PreviewImportRequest(string ModuleKey, byte[] Content) : IRequest<ServiceResult>;
