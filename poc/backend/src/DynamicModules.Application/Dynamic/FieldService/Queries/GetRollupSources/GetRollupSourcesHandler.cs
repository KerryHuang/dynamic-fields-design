using DynamicModules.Application.Common;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.FieldService.Queries.GetRollupSources;

public record RollupSourceResponse(long FieldId, I18nText FieldLabel, string ModuleKey, I18nText ModuleName, string Relation);

/// <summary>所有指向該模組、啟用中的參照欄位（含停用模組、含自己）；設計器挑彙總的來源欄位用。</summary>
public class GetRollupSourcesHandler(IModuleRepository modules) : IRequestHandler<GetRollupSourcesRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetRollupSourcesRequest request, CancellationToken ct)
    {
        if (!await modules.KeyExistsAsync(request.ModuleKey, ct)) return ServiceResult.NotFound("找不到模組");
        var incoming = await modules.ListIncomingReferenceFieldsAsync(request.ModuleKey, ct);
        return ServiceResult.Success(incoming.Select(x => new RollupSourceResponse(
            x.Field.Id, x.Field.LabelI18n, x.Source.Key, x.Source.NameI18n,
            x.Field.Config.Relation == ReferenceRelation.Detail ? "detail" : "lookup")).ToList());
    }
}
