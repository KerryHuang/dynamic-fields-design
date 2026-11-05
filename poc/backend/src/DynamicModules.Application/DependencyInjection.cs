using DynamicModules.Application.Common.Behaviors;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.FieldTypes;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(assembly);
            cfg.AddOpenBehavior(typeof(DesignerAccessBehavior<,>));
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
            cfg.AddOpenBehavior(typeof(TransactionBehavior<,>));
        });
        services.AddValidatorsFromAssembly(assembly);
        services.AddScoped<IDynamicAccessGuard, DynamicAccessGuard>();
        services.AddScoped<Dynamic.SchemaService.Queries.GetModuleSchema.ModuleSchemaBuilder>();
        services.AddScoped<Dynamic.References.ReferenceValidator>();
        services.AddScoped<Dynamic.References.ReferenceResolver>();
        services.AddScoped<Dynamic.References.ReferenceDeletePlanner>();
        services.AddScoped<Dynamic.RecordService.DetailWriter>();
        services.AddScoped<Dynamic.RecordService.DetailReader>();
        services.AddScoped<Dynamic.RecordService.RecordHistoryWriter>();
        services.AddScoped<Dynamic.Rollups.RollupRecalc>();
        services.AddScoped<Dynamic.RecordService.RecordCreator>();
        services.AddScoped<Dynamic.ImportService.ImportPlanner>();
        services.AddSingleton(FieldTypeRegistry.CreateDefault());
        services.AddSingleton<Dynamic.ExportService.ExportOptions>();
        services.AddSingleton(_ => Dynamic.TemplateService.TemplateCatalog.Load(Dynamic.TemplateService.TemplateCatalog.DefaultDirectory));
        services.AddScoped<Dynamic.TemplateService.ITemplateSource, Dynamic.TemplateService.TemplateSource>();
        services.AddScoped<Dynamic.TemplateService.TemplateInstantiator>();
        services.AddScoped<Dynamic.TemplateService.TemplateExporter>();
        return services;
    }
}
