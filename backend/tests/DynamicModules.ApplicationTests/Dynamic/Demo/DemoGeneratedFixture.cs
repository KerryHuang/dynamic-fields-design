using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Acting;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetPagedRecords;
using FluentAssertions;
using MediatR;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

/// <summary>共用同一份產生結果的測試類別放進這個 collection（整個測試回合只產生一次）。</summary>
[CollectionDefinition(Name)]
public sealed class DemoGeneratedCollection : ICollectionFixture<DemoGeneratedFixture>
{
    public const string Name = "Demo generated";
}

/// <summary>產生一次 Demo 資料（Today = 2026-11-05），供只讀的斷言共用。</summary>
public sealed class DemoGeneratedFixture : IAsyncLifetime
{
    public DemoTestHost Host { get; private set; } = null!;

    public ServiceResult Result { get; private set; } = null!;

    /// <summary>產生時每次回報的（完成數、總數）。</summary>
    public List<(int Done, int Total)> Progress { get; } = [];

    public async Task InitializeAsync()
    {
        // 行程內只產生一次（DemoSnapshot）；這裡拿一份複本，產生結果與回報進度取自那次產生
        var snapshot = await DemoSnapshot.GetAsync();
        Result = snapshot.Result;
        Progress.AddRange(snapshot.Progress);
        Host = await DemoSnapshot.CreateHostAsync();
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

    /// <summary>產生失敗時把訊息帶進斷言。</summary>
    public void Generated() => Result.IsSuccess.Should().BeTrue(
        Result.Message + string.Join(";", Result.Errors.Select(e => $"{e.Key}:{string.Join(",", e.Value)}")));

    public static string? Text(DynamicRecordResponse r, string key) =>
        r.Values.GetValueOrDefault(key) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    private static FieldResponse Field(ModuleDetailResponse m, string label) =>
        m.Blocks.SelectMany(b => b.Fields).Single(f => f.Label.Resolve("zh-TW") == label);
}
