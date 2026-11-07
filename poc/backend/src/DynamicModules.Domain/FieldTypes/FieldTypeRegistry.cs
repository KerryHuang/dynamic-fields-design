using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes.Handlers;

namespace DynamicModules.Domain.FieldTypes;

public sealed class FieldTypeRegistry
{
    private readonly Dictionary<FieldType, IFieldTypeHandler> _handlers = [];

    public FieldTypeRegistry(IEnumerable<IFieldTypeHandler> handlers)
    {
        foreach (var handler in handlers)
        {
            if (!_handlers.TryAdd(handler.Type, handler))
                throw new InvalidOperationException($"欄位型別 {handler.Type} 重複註冊處理器");
        }
    }

    public static FieldTypeRegistry CreateDefault() => new(
    [
        new TextHandler(), new TextareaHandler(), new IntegerHandler(), new DecimalHandler(),
        new CurrencyHandler(), new PercentHandler(), new DateHandler(), new DateTimeHandler(),
        new TimeHandler(), new BooleanHandler(), new EmailHandler(), new PhoneHandler(),
        new UrlHandler(), new EnumHandler(), new MultiEnumHandler(), new UserHandler(),
        new FormulaHandler(), new AutoNumberHandler(), new ReferenceHandler(), new RollupHandler(), new AttachmentHandler()
    ]);

    public IFieldTypeHandler Get(FieldType type) =>
        _handlers.TryGetValue(type, out var handler)
            ? handler
            : throw new InvalidOperationException($"找不到欄位型別 {type} 的處理器");
}
