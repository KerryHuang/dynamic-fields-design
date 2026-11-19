using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class MailSettingsRepository(DynamicDbContext db) : IMailSettingsRepository
{
    private DbSet<MailSettings> Rows => db.Set<MailSettings>();

    public Task<MailSettings?> GetAsync(CancellationToken ct) => Rows.AsNoTracking().FirstOrDefaultAsync(m => m.Kind == MailSettings.Default, ct);

    public Task<MailSettings?> GetTrackedAsync(CancellationToken ct) => Rows.FirstOrDefaultAsync(m => m.Kind == MailSettings.Default, ct);

    public void Add(MailSettings settings) => Rows.Add(settings);
}
