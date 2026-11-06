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
            cfg.AddOpenBehavior(typeof(SystemAdminBehavior<,>));
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
            cfg.AddOpenBehavior(typeof(TransactionBehavior<,>));
        });
        services.AddValidatorsFromAssembly(assembly);
        services.AddSingleton<Common.Security.LoginThrottle>();
        services.AddScoped<IDynamicAccessGuard, DynamicAccessGuard>();
        services.AddScoped<Dynamic.SchemaService.Queries.GetModuleSchema.ModuleSchemaBuilder>();
        services.AddScoped<Dynamic.References.ReferenceValidator>();
        services.AddScoped<Dynamic.References.ReferenceResolver>();
        services.AddScoped<Dynamic.References.ReferenceFill>();
        services.AddScoped<Dynamic.WorkflowService.WorkflowEnforcer>();
        services.AddScoped<Dynamic.ConversionService.ConversionLedger>();
        services.AddScoped<Dynamic.ConversionService.ConversionValidator>();
        services.AddScoped<Dynamic.References.ReferenceDeletePlanner>();
        services.AddScoped<Dynamic.RecordService.DetailWriter>();
        services.AddScoped<Dynamic.RecordService.DetailReader>();
        services.AddScoped<Dynamic.RecordService.RecordHistoryWriter>();
        services.AddScoped<Dynamic.Rollups.RollupRecalc>();
        services.AddScoped<Dynamic.RecordService.RecordCreator>();
        services.AddScoped<Dynamic.ImportService.ImportPlanner>();
        services.AddSingleton(FieldTypeRegistry.CreateDefault());
        services.AddSingleton<Dynamic.ExportService.ExportOptions>();
        services.AddSingleton<Dynamic.ReportService.ReportOptions>();
        services.AddScoped<Dynamic.ReportService.ReportRunner>();
        services.AddScoped<Dynamic.ReportService.ReportModuleLoader>();
        services.AddScoped<Dynamic.ReportService.ReportAccess>();
        services.AddScoped<Dynamic.DashboardService.DashboardAccess>();
        services.AddScoped<Dynamic.DashboardService.DashboardCardRunner>();
        services.AddScoped<Dynamic.ReportService.ReportDefinitionValidator>();
        services.AddSingleton(_ => Dynamic.TemplateService.TemplateCatalog.Load(Dynamic.TemplateService.TemplateCatalog.DefaultDirectory));
        services.AddScoped<Dynamic.TemplateService.ITemplateSource, Dynamic.TemplateService.TemplateSource>();
        services.AddScoped<Dynamic.TemplateService.TemplateInstantiator>();
        services.AddScoped<Dynamic.TemplateService.TemplateExporter>();
        services.AddScoped<Dynamic.DemoService.DemoPreflight>();
        services.AddScoped<Dynamic.DemoService.DemoStructure>();
        services.AddScoped<Dynamic.DeleteService.DeletePlanner>();
        return services;
    }
}
