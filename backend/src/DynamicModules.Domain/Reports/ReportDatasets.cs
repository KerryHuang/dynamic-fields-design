namespace DynamicModules.Domain.Reports;

/// <summary>報表的資料來源（<see cref="ReportDefinition.Dataset"/>）：省略或 module 是模組報表；tasks、taskEvents 是任務（看板設計 §7.1）。</summary>
public static class ReportDatasets
{
    public const string Module = "module", Tasks = "tasks", TaskEvents = "taskEvents";

    public static bool IsModule(string? dataset) => dataset is null or Module;

    public static bool IsTask(string? dataset) => dataset is Tasks or TaskEvents;
}
