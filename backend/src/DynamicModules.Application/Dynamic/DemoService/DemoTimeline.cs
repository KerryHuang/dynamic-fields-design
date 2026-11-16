using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Acting;
using DynamicModules.Application.Dynamic.ConversionService.Commands.ConvertRecord;
using DynamicModules.Application.Dynamic.DemoService.Scenarios;
using DynamicModules.Application.Dynamic.LedgerService;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;

namespace DynamicModules.Application.Dynamic.DemoService;

/// <summary>時間軸上的一步：在 At 以 UserId 的身分執行 Run。Label 用在失敗訊息。</summary>
public sealed record DemoStep(DateTime At, string UserId, string Label, Func<DemoContext, CancellationToken, Task<ServiceResult>> Run);

/// <summary>一個情境的規劃器：收集步驟；At ≥ 今天 00:00 的步驟不加（已定決策 3）。</summary>
public sealed class DemoPlan(DateOnly today, Random random, IReadOnlyDictionary<string, IReadOnlyList<DemoRecord>> records)
{
    private const int WorkMinutes = 10 * 60; // 08:00–17:59

    private readonly List<DemoStep> _steps = [];
    private readonly List<DemoRecord> _created = [];

    public DateOnly Today { get; } = today;
    public Random Random { get; } = random;

    /// <summary>前面情境建立的記錄，鍵是模組代號。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<DemoRecord>> Records { get; } = records;

    public IReadOnlyList<DemoStep> Steps => _steps;

    /// <summary>這個情境規劃了建立步驟的記錄。</summary>
    public IReadOnlyList<DemoRecord> Created => _created;

    public DemoRecord Create(string moduleCode, DateTime at, string userId, string label, Func<DemoContext, CreateRecordRequest> build)
    {
        var record = new DemoRecord(moduleCode, DateOnly.FromDateTime(at));
        if (Add(at, userId, label, async (ctx, ct) => Fill(record, await ctx.Sender.Send(build(ctx), ct))))
            _created.Add(record);
        return record;
    }

    /// <summary>單據轉換：產生的目標記錄回填到回傳的 DemoRecord，後面的步驟可以接著更新它。</summary>
    public DemoRecord Convert(string moduleCode, DateTime at, string userId, string label, Func<DemoContext, ConvertRecordRequest> build)
    {
        var record = new DemoRecord(moduleCode, DateOnly.FromDateTime(at));
        if (Add(at, userId, label, async (ctx, ct) => Fill(record, await ctx.Sender.Send(build(ctx), ct))))
            _created.Add(record);
        return record;
    }

    public void Update(DemoRecord r, DateTime at, string userId, string label, Func<DemoContext, DemoRecord, UpdateRecordRequest> build) =>
        Add(at, userId, label, async (ctx, ct) => Fill(r, await ctx.Sender.Send(build(ctx, r), ct)));

    /// <summary>沖銷不可改的分錄記錄（反向分錄由系統建立，不回填到 DemoRecord）。</summary>
    public void Reverse(DemoRecord r, DateTime at, string userId, string label) =>
        Add(at, userId, label, async (ctx, ct) => await ctx.Sender.Send(new ReverseRecordRequest(r.Id, null), ct));

    /// <summary>重讀記錄、回填 RowVersion：別的記錄寫入觸發彙總重算（會換這筆的 RowVersion）之後，這筆還要再改時用。</summary>
    public void Refresh(DemoRecord r, DateTime at, string userId, string label) =>
        Add(at, userId, label, async (ctx, ct) => Fill(r, await ctx.Sender.Send(new GetRecordRequest(r.Id), ct)));

    /// <summary>不回填記錄的一般步驟（例如在任務上留言）；run 回傳失敗時整個產生停下。</summary>
    public void Run(DateTime at, string userId, string label, Func<DemoContext, CancellationToken, Task<ServiceResult>> run) =>
        Add(at, userId, label, run);

    /// <summary>同一天依種子取 08:00–17:59 的時刻（分鐘為單位）。</summary>
    public DateTime At(DateOnly date) => date.ToDateTime(new TimeOnly(8, 0)).AddMinutes(Random.Next(WorkMinutes));

    /// <summary>同 At(date)，但保證晚於 after：date 早於 after 當天就改用 after 當天；同一天已經沒有空檔就挪到隔天。</summary>
    public DateTime At(DateOnly date, DateTime after)
    {
        var afterDay = DateOnly.FromDateTime(after);
        if (date < afterDay) date = afterDay;
        var start = date.ToDateTime(new TimeOnly(8, 0));
        if (after < start) return At(date);
        var from = (int)(after - start).TotalMinutes + 1;
        return from >= WorkMinutes ? At(date.AddDays(1)) : start.AddMinutes(Random.Next(from, WorkMinutes));
    }

    private bool Add(DateTime at, string userId, string label, Func<DemoContext, CancellationToken, Task<ServiceResult>> run)
    {
        if (DateOnly.FromDateTime(at) >= Today) return false;
        _steps.Add(new DemoStep(at, userId, label, run));
        return true;
    }

    private static ServiceResult Fill(DemoRecord record, ServiceResult r)
    {
        if (r.IsSuccess) record.Fill((DynamicRecordResponse)r.Data!);
        return r;
    }
}

public static class DemoTimeline
{
    /// <summary>依情境順序規劃，每個情境各用 new Random(Seed)；回傳的順序是（情境順序、加入順序）。</summary>
    public static IReadOnlyList<DemoStep> Plan(DateOnly today, IEnumerable<IDemoScenario> scenarios)
    {
        var records = new Dictionary<string, IReadOnlyList<DemoRecord>>();
        var steps = new List<DemoStep>();
        foreach (var scenario in scenarios)
        {
            var plan = new DemoPlan(today, new Random(scenario.Seed), records);
            scenario.Plan(plan);
            steps.AddRange(plan.Steps);
            foreach (var g in plan.Created.GroupBy(r => r.ModuleCode))
                records[g.Key] = [.. records.GetValueOrDefault(g.Key) ?? [], .. g];
        }
        return steps;
    }

    /// <summary>
    /// 依（At、情境順序、加入順序）穩定排序後逐步執行，每步以該步的使用者與模擬時間送出。
    /// 任何一步失敗就停，回 400（訊息以 Label 開頭）；全部成功回 null。
    /// </summary>
    public static async Task<ServiceResult?> RunAsync(
        DemoContext ctx, IReadOnlyList<DemoStep> steps, IProgress<(int, int)>? progress, CancellationToken ct)
    {
        var ordered = steps.OrderBy(s => s.At).ToList(); // OrderBy 是穩定排序，同時間保留原順序
        for (var i = 0; i < ordered.Count; i++)
        {
            var step = ordered[i];
            ServiceResult r;
            ctx.BeginStep?.Invoke();
            using (ActingScope.Begin(step.UserId, step.At))
                r = await step.Run(ctx, ct);
            if (!r.IsSuccess)
                return ServiceResult.Invalid(new Dictionary<string, string[]> { ["demo"] = [$"{step.Label}：{Summary(r)}"] });
            progress?.Report((i + 1, ordered.Count));
        }
        return null;
    }

    private static string Summary(ServiceResult r) =>
        r.Message ?? string.Join("；", r.Errors.SelectMany(e => e.Value.Select(v => $"{e.Key}: {v}")));
}
