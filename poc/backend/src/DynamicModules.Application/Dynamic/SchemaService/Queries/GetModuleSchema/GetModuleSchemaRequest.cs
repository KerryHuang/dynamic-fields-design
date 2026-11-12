using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.SchemaService.Queries.GetModuleSchema;

/// <param name="RecordId">簽核人沒有模組讀取權時，用這一筆記錄的臨時讀取權取 schema（簽核設計 §10.1）；有讀取權時忽略。</param>
public record GetModuleSchemaRequest(string ModuleKey, long? RecordId = null) : IRequest<ServiceResult>;
