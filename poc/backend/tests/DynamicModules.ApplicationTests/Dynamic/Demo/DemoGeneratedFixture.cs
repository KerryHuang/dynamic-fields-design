using System.Diagnostics;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Acting;
using DynamicModules.Application.Dynamic.DemoService.Commands.GenerateDemoData;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetPagedRecords;
using MediatR;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

/// <summary>產生一次 Demo 資料（Today = 2026-11-05），供只讀的斷言共用。</summary>
public sealed class DemoGeneratedFixture : IAsyncLifetime
{
    public DemoTestHost Host { get; private set; } = null!;

    public ServiceResult Result { get; private set; } = null!;

    /// <summary>產生一次的耗時（不含建測試庫與種子）。</summary>
    public TimeSpan Elapsed { get; private set; }

    public async Task InitializeAsync()
    {
        Host = await DemoTestHost.CreateAsync();
        var watch = Stopwatch.StartNew();
        Result = await Host.Send(new GenerateDemoDataRequest(DemoTestHost.Today));
        Elapsed = watch.Elapsed;
    }

    public async Task DisposeAsync() => await Host.DisposeAsync();

    /// <summary>以指定身分送查詢。</summary>
    public async Task<TRes> As<TRes>(string userId, IRequest<TRes> request)
    {
        using var _ = ActingScope.Begin(userId);
        return await Host.Send(request);
    }

    /// <summary>以指定身分取模組全部記錄（一頁 200 筆）。</summary>
    public async Task<IReadOnlyList<DynamicRecordResponse>> Records(string moduleKey, string userId = "admin")
    {
        var r = await As(userId, new GetPagedRecordsRequest(moduleKey, 1, 200, null, [], [], null));
        return ((PagedResponse<DynamicRecordResponse>)r.Data!).Items;
    }

    public async Task<ModuleDetailResponse> Module(string key) =>
        (ModuleDetailResponse)(await Host.Send(new GetModuleRequest(key))).Data!;

    /// <summary>標籤（zh-TW）→ Field.Key。</summary>
    public async Task<string> Key(string module, string label) => Field(await Module(module), label).Key;

    /// <summary>欄位的選項標籤（zh-TW）→ 選項值 opt_&lt;id&gt;。</summary>
    public async Task<string> Opt(string module, string label, string option) =>
        Field(await Module(module), label).Options.Single(o => o.Label.Resolve("zh-TW") == option).Value;

    public static string? Text(DynamicRecordResponse r, string key) =>
        r.Values.GetValueOrDefault(key) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    private static FieldResponse Field(ModuleDetailResponse m, string label) =>
        m.Blocks.SelectMany(b => b.Fields).Single(f => f.Label.Resolve("zh-TW") == label);
}
