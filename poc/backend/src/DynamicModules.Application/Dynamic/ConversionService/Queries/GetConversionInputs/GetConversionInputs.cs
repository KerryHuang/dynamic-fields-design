using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.ConversionService.Commands.ConvertRecord;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ConversionService.Queries.GetConversionInputs;

public record GetConversionInputsRequest(long RecordId, string ConversionKey) : IRequest<ServiceResult>;

/// <summary>
/// 轉換時使用者得自己補的目標表頭欄位：用跟轉換相同的帶值規則組出表頭，對目標模組做一次不寫入的驗證，
/// 只留下「必填卻沒有值」且使用者自己能寫的欄位（使用者寫不了的欄位不檢查必填，不會擋住轉換）。回欄位鍵清單，欄位定義前端從目標模組取。
/// </summary>
public class GetConversionInputsHandler(
    IConversionRepository conversions, IModuleRepository modules, IRecordRepository records, IDynamicAccessGuard guard, RecordCreator creator)
    : IRequestHandler<GetConversionInputsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetConversionInputsRequest request, CancellationToken ct)
    {
        var conversion = await conversions.GetByKeyAsync(request.ConversionKey, ct);
        if (conversion is not { Active: true }) return ServiceResult.NotFound(ConvertRecordHandler.NotFound);
        var record = await records.GetAsync(request.RecordId, ct);
        if (record is null || record.ModuleId != conversion.SourceModuleId) return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        var source = await modules.GetByIdAsync(conversion.SourceModuleId, includeChildren: true, ct);
        var target = await modules.GetByIdAsync(conversion.TargetModuleId, includeChildren: true, ct);
        if (!source.IsReadable() || !target.IsReadable()) return ServiceResult.NotFound(RecordMapper.ModuleNotFound);

        var access = await guard.AuthorizeAsync(source, PermissionAction.Read, ct);
        if (access.Permissions is not { } perms) return access.Denied!;
        if (perms.OwnerUserIds is not null && !perms.OwnerUserIds.Contains(record.OwnerUserId))
            return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        var addAccess = await guard.AuthorizeAsync(target, PermissionAction.Add, ct);
        if (addAccess.Permissions is not { } addPerms) return addAccess.Denied!;

        var def = conversion.Definition;
        var sourceFields = source.Blocks.SelectMany(b => b.Fields).ToDictionary(f => f.Id);
        var targetFields = target.Blocks.SelectMany(b => b.Fields).ToDictionary(f => f.Id);
        var (values, systemKeys) = ConvertRecordHandler.CopyValues(
            def.Header.Copy, sourceFields, perms, targetFields, addPerms, RecordValueProcessor.Parse(record.ValuesJson), new Dictionary<string, JsonElement?>());
        var traceKey = $"f_{def.Header.SourceFieldId}";
        values[traceKey] = JsonSerializer.SerializeToElement(record.Id);
        systemKeys.Add(traceKey);

        var (_, errors) = await creator.ValidateAsync(target, addPerms, values, null, ct, systemKeys);
        var keys = target.Blocks.SelectMany(b => b.Fields).Where(f => errors.TryGetValue(f.Key, out var m) && m is [RecordValueProcessor.RequiredMessage])
            .Select(f => f.Key).ToList();
        return ServiceResult.Success(keys);
    }
}
