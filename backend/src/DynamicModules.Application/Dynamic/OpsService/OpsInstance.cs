using System.Text.RegularExpressions;

namespace DynamicModules.Application.Dynamic.OpsService;

/// <summary>這個程序的身分：機器名、程序號與隨機碼，重啟後會變（租約持有者與 ops info 用）。</summary>
public sealed partial class OpsInstance
{
    public string InstanceId { get; }
    public DateTime StartedAtUtc { get; }

    private OpsInstance(string id, DateTime startedAtUtc) { InstanceId = id; StartedAtUtc = startedAtUtc; }

    public static OpsInstance Create(TimeProvider time)
    {
        var machine = Unsafe().Replace(Environment.MachineName.ToLowerInvariant(), "-");
        if (machine.Length > 80) machine = machine[..80];
        var suffix = Convert.ToHexString(Guid.NewGuid().ToByteArray(), 0, 2).ToLowerInvariant();
        return new OpsInstance($"{machine}-{Environment.ProcessId}-{suffix}", time.GetUtcNow().UtcDateTime);
    }

    [GeneratedRegex("[^a-z0-9-]")] private static partial Regex Unsafe();
}
