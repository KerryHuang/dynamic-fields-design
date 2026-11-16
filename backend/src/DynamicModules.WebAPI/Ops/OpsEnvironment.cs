using DynamicModules.Application.Dynamic.OpsService;

namespace DynamicModules.WebAPI.Ops;

public sealed class OpsEnvironment : IOpsEnvironment
{
    public OpsEnvironment(IConfiguration config, ILogger<OpsEnvironment> logger)
    {
        var raw = config["Ops:Edition"];
        Edition = raw is "Cloud" or "OnPrem" ? raw : "OnPrem";
        if (!string.IsNullOrEmpty(raw) && raw != Edition)
            logger.LogWarning("Ops:Edition 只接受 Cloud 或 OnPrem，收到 {Value}，改用 OnPrem", raw);
    }

    public string Version => BuildInfo.Version;
    public string Commit => BuildInfo.Commit;
    public string BuiltAt => BuildInfo.BuiltAt;
    public string Edition { get; }
}
