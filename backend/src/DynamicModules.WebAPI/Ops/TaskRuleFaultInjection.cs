using DynamicModules.Application.Dynamic.OpsService.RulePause;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DynamicModules.WebAPI.Ops;

/// <summary>
/// 任務規則的故障注入（維運設計 §17.2.7、O2-T16，只給驗收用）：部署設定 <c>Ops:FaultInjection:TaskRuleKeys</c>（規則代碼，逗號分隔）
/// 只在開發環境生效，命中的規則每次評估都丟例外；有設定就在啟動時寫 Warning。其他環境設了也不生效，同樣寫 Warning 提醒拿掉。
/// </summary>
public static class TaskRuleFaultInjection
{
    public const string Key = "Ops:FaultInjection:TaskRuleKeys";

    public static void Configure(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment, List<string> warnings)
    {
        var keys = (configuration[Key] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0) return;
        if (!environment.IsDevelopment())
        {
            warnings.Add($"{Key} 只在開發環境生效，目前環境 {environment.EnvironmentName} 不注入故障；請拿掉這個設定");
            return;
        }
        services.Replace(ServiceDescriptor.Singleton<ITaskRuleFaultInjector>(new ConfiguredTaskRuleFaults(keys)));
        warnings.Add($"{Key} 已開啟：任務規則 {string.Join(", ", keys)} 每次評估都會失敗（故障注入，只能用在驗收）");
    }
}
