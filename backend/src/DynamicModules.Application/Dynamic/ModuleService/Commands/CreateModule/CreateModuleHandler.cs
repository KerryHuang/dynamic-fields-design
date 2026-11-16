using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;

public class CreateModuleHandler(
    IModuleRepository modules, IProgramRepository programs, IUnitOfWork uow,
    ICurrentUser user, IBusinessTime time, ReferenceProviderRegistry providers) : IRequestHandler<CreateModuleRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CreateModuleRequest request, CancellationToken ct)
    {
        if (await modules.KeyExistsAsync(request.Key, ct)) return ServiceResult.Conflict("模組代碼已存在");

        var isExtension = request.Kind == ModuleKind.Extension;
        if (isExtension)
        {
            if (request.HostEntityKey is not { } host || providers.Get(host) is null)
                return ServiceResult.Invalid(new Dictionary<string, string[]> { ["HostEntityKey"] = ["擴充模組必須指定既有實體"] });
            if (await modules.HostEntityExistsAsync(host, ct)) return ServiceResult.Conflict("這個既有實體已經有擴充模組");
        }

        var now = time.Now;
        var programCode = await programs.NextModuleProgramCodeAsync(ct);
        var program = new DynamicProgram
        {
            ProgramCode = programCode,
            NameI18n = new I18nText(request.Name.Values),
            IsBeta = true,
            Active = false
        };
        programs.Add(program);

        var block = new Block
        {
            LabelI18n = Text("基本資料", "General"),
            Sequence = 1,
            Columns = Block.DefaultColumns
        };
        block.StampCreated(user.UserId, now);
        var module = new Module
        {
            Key = request.Key,
            ProgramCode = programCode,
            NameI18n = request.Name,
            Icon = request.Icon,
            Kind = request.Kind,
            HostEntityKey = isExtension ? request.HostEntityKey : null,
            Status = ModuleStatus.Draft,
            Version = 1,
            Blocks = [block]
        };
        module.SetSection(request.Section);
        module.StampCreated(user.UserId, now);
        modules.Add(module);
        await uow.CommitAsync(ct);
        if (isExtension || !request.CreateTitleField) return ServiceResult.Created(module.ToResponse());

        // Field.Key = f_<Id> 需先有 Id；(ModuleId, Key) 唯一，所以先以空 Key 插入再回填。
        var field = new Field
        {
            ModuleId = module.Id,
            BlockId = block.Id,
            Type = FieldType.Text,
            LabelI18n = Text("名稱", "Name"),
            Config = new FieldConfig { Required = true, ShowInList = true },
            Sequence = 1,
            Status = FieldStatus.Active
        };
        field.StampCreated(user.UserId, now);
        block.Fields.Add(field);
        await uow.CommitAsync(ct);

        field.Key = $"f_{field.Id}";
        module.TitleFieldId = field.Id;
        await uow.CommitAsync(ct);

        return ServiceResult.Created(module.ToResponse());
    }

    private static I18nText Text(string zh, string en) =>
        new([KeyValuePair.Create("zh-TW", zh), KeyValuePair.Create("en-US", en)]);
}
