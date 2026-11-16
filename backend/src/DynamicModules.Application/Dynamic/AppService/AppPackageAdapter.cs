using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.TemplateService.Data;

namespace DynamicModules.Application.Dynamic.AppService;

/// <summary>
/// 把 v2 套件包成一個 v1 的 TemplatePackage，讓既有的 TemplateValidator、TemplateGraph、TemplateInstantiator 原封不動。
/// 這是不動既有程式的捷徑：FormatVersion 在這裡設成 1，TemplateValidator 才會放行。
/// </summary>
public static class AppPackageAdapter
{
    public static TemplatePackage ToTemplate(AppPackageData p) =>
        new(1, p.Manifest.Id, p.Manifest.Name, p.Manifest.Description, p.Modules, null);
}
