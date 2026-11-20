namespace DynamicModules.Application.Dynamic.OpsService;

/// <summary>建置與部署資訊（版本由建置注入，Edition 讀部署設定 Ops:Edition）。</summary>
public interface IOpsEnvironment
{
    /// <summary>建置沒注入版本時的值（沒有發版流程時本機與 Demo 都是它；語系包的 <c>builtFor</c> 遇到它不比對）。</summary>
    public const string DevVersion = "dev";

    string Version { get; }
    string Commit { get; }
    string BuiltAt { get; }
    /// <summary><c>Cloud</c> 或 <c>OnPrem</c>。</summary>
    string Edition { get; }
}
