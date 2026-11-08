using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;

public record DeleteRecordRequest(long Id, string RowVersion) : IRequest<ServiceResult>, IRecordWriteRequest;
