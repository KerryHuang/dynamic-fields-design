using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.ImportService.Commands.ImportRecords;

/// <summary>確認匯入（commit=true）：重新解析、驗證整個檔案，全部通過才整批新增。</summary>
public record ImportRecordsRequest(string ModuleKey, byte[] Content) : IRequest<ServiceResult>, IRecordWriteRequest;
