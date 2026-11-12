namespace DynamicModules.Application.Dynamic.OpsService;

/// <summary>建置與部署資訊（版本由建置注入，Edition 讀部署設定 Ops:Edition）。</summary>
public interface IOpsEnvironment
{
    string Version { get; }
    string Commit { get; }
    string BuiltAt { get; }
    /// <summary><c>Cloud</c> 或 <c>OnPrem</c>。</summary>
    string Edition { get; }
}
