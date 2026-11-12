using System.Reflection;

namespace DynamicModules.WebAPI.Ops;

/// <summary>建置時注入的版本資訊（AssemblyMetadata）；沒注入時三個值都是 dev。</summary>
public static class BuildInfo
{
    private const string Dev = "dev";

    public static string Version { get; } = Read("AppVersion");
    public static string Commit { get; } = Read("GitCommit");
    public static string BuiltAt { get; } = Read("BuildTime");

    private static string Read(string key) =>
        typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } v ? v : Dev;
}
