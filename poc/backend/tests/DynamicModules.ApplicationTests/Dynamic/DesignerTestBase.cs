using System.Text.Json;
using DynamicModules.Application;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.AttachmentService;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.FieldService.Commands.SetFieldStatus;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateField;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.ApplicationTests.Persistence;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Files;
using DynamicModules.Persistence.Seed;
using DynamicModules.TestSupport;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>設計器驗證測試共用：SQLite、MediatR、建模組與欄位的小工具。</summary>
public abstract class DesignerTestBase : IAsyncLifetime
{
    protected SqliteTestDb Db = null!;
    private ServiceProvider _sp = null!;

    private sealed class FakeUser : ICurrentUser { public string UserId { get; set; } = "admin"; }

    private readonly FakeUser _user = new();

    /// <summary>附件的本機儲存根目錄（每個測試一個，結束時刪除）；模組清除與留言附件會用到。</summary>
    private readonly string _attachmentRoot = Path.Combine(Path.GetTempPath(), $"dm-app-att-{Guid.NewGuid():N}");

    /// <summary>切換目前使用者（預設 admin）。</summary>
    protected void As(string userId) => _user.UserId = userId;

    private sealed class FakeTime : IBusinessTime
    {
        public DateTime Now => new(2026, 11, 4, 9, 0, 0);
        public DateOnly Today => DateOnly.FromDateTime(Now);
    }

    /// <summary>
    /// 開啟時，經 <see cref="AddField"/> 建立的欄位一律勾「可篩選」（有索引），並在啟動時寫入索引回填版本列：
    /// 報表測試用它讓同一組測試改走 SQL 聚合路徑再跑一次（報表資料庫端聚合設計 §7 第 1 項）。
    /// </summary>
    protected virtual bool FieldsFilterable => false;

    /// <summary>子類別追加或覆寫服務（例如註冊測試用的假外掛）。</summary>
    protected virtual void ConfigureServices(IServiceCollection services) { }

    public async Task InitializeAsync()
    {
        Db = await SqliteTestDb.CreateAsync();
        await SeedData.EnsureSeededAsync(Db.Context);
        await DemoPeople.EnsureAsync(Db.Context);
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddPersistenceRepositories();
        services.AddTestEntities();
        services.AddSingleton(Db.Context);   // 執行個體註冊：scope 結束時容器不會把共用的 DbContext 釋放掉
        services.AddScoped<ITransactionManager, EfTransactionManager>();
        services.AddScoped<IWorkSavepoints, EfWorkSavepoints>();
        services.AddSingleton<ICurrentUser>(_user);
        services.AddSingleton<IBusinessTime, FakeTime>();
        services.AddSingleton(new AuthSettings(false));
        services.AddSingleton(new AttachmentSettings(_attachmentRoot, true, null));
        services.AddSingleton<IAttachmentStore>(new LocalAttachmentStore(_attachmentRoot));
        ConfigureServices(services);
        _sp = services.BuildServiceProvider();
        if (FieldsFilterable)
        {
            Db.Context.Add(new SystemSetting
            {
                Key = SystemSetting.IndexBackfillVersion, Value = DynamicModules.Application.Dynamic.RecordService.RecordIndexer.BackfillVersion
            });
            await Db.Context.SaveChangesAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        await Db.DisposeAsync();
        if (Directory.Exists(_attachmentRoot)) Directory.Delete(_attachmentRoot, recursive: true);
    }

    protected T Svc<T>() where T : notnull => _sp.GetRequiredService<T>();

    /// <summary>每個請求自己一個 DI scope（寫入日誌、操作識別碼都是每個請求一份，與正式環境一致）；預設共用根 scope。</summary>
    protected virtual bool ScopePerRequest => false;

    /// <summary>測試要直接驅動請求內部元件（例如寫入後盾）時，自己開一個請求等級的 scope。</summary>
    protected IServiceScope NewScope() => _sp.CreateScope();

    /// <summary>ScopePerRequest 時，每個請求帶的 Idempotency-Key（null 為不帶）。</summary>
    protected string? ClientKey { get; set; }

    protected async Task<TRes> Send<TRes>(IRequest<TRes> request)
    {
        if (!ScopePerRequest) return await _sp.GetRequiredService<IMediator>().Send(request);
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<IOperationContext>().ClientKey = ClientKey;
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }

    protected static I18nText Text(string zh) => new([KeyValuePair.Create("zh-TW", zh)]);

    protected static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    protected static string Err(ServiceResult r, string key) => r.Errors[key][0];

    protected async Task<ModuleDetailResponse> Detail(string key) =>
        (ModuleDetailResponse)(await Send(new GetModuleRequest(key))).Data!;

    /// <summary>建立模組（自帶標題欄位）；activate 時一併啟用。</summary>
    protected async Task<string> NewModule(string key, bool activate = false, ModuleKind kind = ModuleKind.Standalone, string? host = null)
    {
        var r = await Send(new CreateModuleRequest(key, Text(key), null, null, kind, host));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        if (activate) (await Send(new ActivateModuleRequest(key))).IsSuccess.Should().BeTrue();
        return key;
    }

    protected async Task<ServiceResult> AddField(string module, FieldType type, FieldConfig? config = null, string label = "欄位")
    {
        var m = await Detail(module);
        config ??= new FieldConfig();
        if (FieldsFilterable) config = config with { IsFilterable = true };
        return await Send(new AddFieldRequest(module, m.Blocks[0].Id, type, Text(label), config));
    }

    /// <summary>新增並要求成功，回傳欄位回應。</summary>
    protected async Task<FieldResponse> MustAdd(string module, FieldType type, FieldConfig? config = null, string label = "欄位")
    {
        var r = await AddField(module, type, config, label);
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (FieldResponse)r.Data!;
    }

    protected Task<ServiceResult> Update(FieldResponse f, FieldConfig config) =>
        Send(new UpdateFieldRequest(f.Id, f.Type, f.Label, config, f.BlockId));

    protected Task<ServiceResult> Deactivate(long fieldId) => Send(new SetFieldStatusRequest(fieldId, FieldStatus.Inactive));

    protected static FieldConfig RefTo(string moduleKey, ReferenceRelation? relation = null, ReferenceOnDelete? onDelete = null) =>
        new() { RefTarget = new ReferenceTarget(ReferenceTarget.ModuleKind, moduleKey), Relation = relation, OnDelete = onDelete };

    protected async Task<string> CreateRecord(string module)
    {
        var m = await Detail(module);
        var r = await Send(new CreateRecordRequest(module,
            new Dictionary<string, JsonElement?> { [$"f_{m.TitleFieldId}"] = J("\"x\"") }));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return module;
    }
}
