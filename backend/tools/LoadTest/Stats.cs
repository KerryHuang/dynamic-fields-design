using System.Text;

namespace LoadTest;

public sealed record OpStats(string Op, int Count, int Errors, double P50, double P95, double P99, double Max, Dictionary<string, int> ErrorKinds);

public sealed record PhaseResult(string Phase, int Users, double Seconds, double Throughput, List<OpStats> Ops);

/// <summary>執行緒安全的延遲與錯誤收集；錯誤依訊息前 70 字歸類。</summary>
public sealed class Recorder
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<double>> _ok = new();
    private readonly Dictionary<string, Dictionary<string, int>> _errors = new();

    public void Ok(string op, double ms)
    {
        lock (_gate)
        {
            if (!_ok.TryGetValue(op, out var l)) _ok[op] = l = [];
            l.Add(ms);
        }
    }

    public void Error(string op, double ms, string message)
    {
        var kind = message.ReplaceLineEndings(" ");
        if (kind.Length > 200) kind = kind[..200];
        lock (_gate)
        {
            if (!_errors.TryGetValue(op, out var e)) _errors[op] = e = new();
            e[kind] = e.GetValueOrDefault(kind) + 1;
        }
    }

    public List<OpStats> Summarize()
    {
        lock (_gate)
        {
            return _ok.Keys.Union(_errors.Keys).Order().Select(op =>
            {
                var ok = _ok.GetValueOrDefault(op)?.Order().ToList() ?? [];
                var err = _errors.GetValueOrDefault(op) ?? new();
                double Pct(double p) => ok.Count == 0 ? double.NaN : ok[Math.Min(ok.Count - 1, (int)Math.Ceiling(p * ok.Count) - 1)];
                return new OpStats(op, ok.Count + err.Values.Sum(), err.Values.Sum(), Pct(.5), Pct(.95), Pct(.99),
                    ok.Count == 0 ? double.NaN : ok[^1], err);
            }).ToList();
        }
    }

    public static string Table(PhaseResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"== {r.Phase}（{r.Users} 人，{r.Seconds:F0}s，吞吐 {r.Throughput:F1} req/s）");
        sb.AppendLine($"{"操作",-22}{"次數",7}{"錯誤",6}{"p50ms",9}{"p95ms",9}{"p99ms",9}{"maxms",9}");
        foreach (var o in r.Ops)
        {
            sb.AppendLine($"{o.Op,-22}{o.Count,7}{o.Errors,6}{o.P50,9:F1}{o.P95,9:F1}{o.P99,9:F1}{o.Max,9:F1}");
            foreach (var (k, n) in o.ErrorKinds) sb.AppendLine($"    ! {n}× {k}");
        }
        return sb.ToString();
    }
}
