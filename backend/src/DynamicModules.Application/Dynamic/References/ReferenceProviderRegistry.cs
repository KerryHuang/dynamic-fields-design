namespace DynamicModules.Application.Dynamic.References;

public class ReferenceProviderRegistry(IEnumerable<IReferenceProvider> providers)
{
    private readonly IReadOnlyList<IReferenceProvider> _all = providers.ToList();

    public IReadOnlyList<IReferenceProvider> All => _all;

    public IReferenceProvider? Get(string entityKey) => _all.FirstOrDefault(p => p.EntityKey == entityKey);
}
