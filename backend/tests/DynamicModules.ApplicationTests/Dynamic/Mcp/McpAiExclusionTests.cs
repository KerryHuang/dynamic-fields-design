using System.Text;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.ExportService.Queries.ExportRecords;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetPagedRecords;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecordHistory;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.ApplicationTests.Dynamic.Approvals;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Mcp;

/// <summary>
/// 「不提供給 AI」在 MCP 通道的權限解析遮罩（mcp1a 計畫判斷 10、Q3、Q7，Task 5）。每個「MCP 被遮」的案例都對照畫面通道的既有結果不變。
/// 情境：單據 po（薪資標旗標、薪資公式＝薪資 × 1、獎金對 buyers 隱藏）；明細單價標旗標，所以小計（數量 × 單價）、明細合計、總額、折扣後總額
/// 都依賴它；另加「單價合計」（加總對象是單價）、「高價筆數」（計數、條件是單價 &gt; 100）、「數量筆數」（計數、條件是數量，不受影響）。
/// buyers（buyer1、buyer2）有單據全部權限；fin1 只有簽核任務。
/// </summary>
[Trait("Category", "Postgres")]
public class McpAiExclusionTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private readonly TestRequestChannel _channel = new();
    private PurchaseRig _r = null!;
    private long _salary, _salaryX, _bonus, _priceSum, _countCond, _countQty, _buyers;

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton<IRequestChannel>(_channel);
        services.AddChannelPermissionMasking();
    }

    private async Task Rig(bool flag = true)
    {
        _r = await BuildPurchase();
        _salary = (await MustAdd(_r.Module, FieldType.Decimal, new FieldConfig { IsFilterable = true, IsSortable = true, Scale = 0 }, "薪資")).Id;
        _salaryX = (await MustAdd(_r.Module, FieldType.Formula,
            new FieldConfig { Formula = $"{{{_salary}}} * 1", ResultType = "number", Scale = 0 }, "薪資公式")).Id;
        _bonus = (await MustAdd(_r.Module, FieldType.Decimal, new FieldConfig { IsFilterable = true, Scale = 0 }, "獎金")).Id;
        _priceSum = (await MustAdd(_r.Module, FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(_r.LineHead, RollupFunction.Sum, _r.LinePrice, null) }, "單價合計")).Id;
        _countCond = (await MustAdd(_r.Module, FieldType.Rollup, new FieldConfig
        {
            Rollup = new RollupConfig(_r.LineHead, RollupFunction.Count, null, J($$"""{"field":{{_r.LinePrice}},"op":"gt","value":100}"""))
        }, "高價筆數")).Id;
        _countQty = (await MustAdd(_r.Module, FieldType.Rollup, new FieldConfig
        {
            Rollup = new RollupConfig(_r.LineHead, RollupFunction.Count, null, J($$"""{"field":{{_r.LineQty}},"op":"gt","value":0}"""))
        }, "數量筆數")).Id;
        await SeedFlow(_r, "po-g", _r.Confirmed, 1, Def(null, Stage("fin", Group(FinanceGroup), editable: [_salary, _bonus])));
        (await SaveWorkflow(_r, [Needs(_r.Draft, _r.Confirmed), Plain(_r.Confirmed, _r.Partial), Plain(_r.Partial, _r.Received),
            Plain(_r.Confirmed, _r.Cancelled)])).IsSuccess.Should().BeTrue();

        var group = new PermissionGroup { Key = "buyers", NameI18n = Text("buyers") };
        Db.Context.Add(group);
        await Db.Context.SaveForTestsAsync();
        _buyers = group.Id;
        Db.Context.AddRange(new UserGroupMapping { GroupId = _buyers, UserId = "buyer1" }, new UserGroupMapping { GroupId = _buyers, UserId = "buyer2" });
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();
        As("admin");
        foreach (var m in new[] { _r.Module, _r.Extra })
        {
            var fields = m == _r.Module ? new[] { new FieldPermissionDto(_bonus, _buyers, FieldAccess.Hidden) } : [];
            var res = await Send(new SaveModulePermissionsRequest(m, new ModulePermissionsDto(
                [new GroupPermissionDto(_buyers, new PrivilegesDto(true, true, true, true, true, true, false), DataScope.All)], [], [.. fields], [])));
            res.IsSuccess.Should().BeTrue(Dump(res));
        }
        if (flag) await Exclude(_salary, _r.LinePrice);
    }

    /// <summary>直接在資料庫標「不提供給 AI」（測試許可；正式路徑的寫入規則見 McpAiExclusionWriteRuleTests）。</summary>
    private async Task Exclude(params long[] fieldIds)
    {
        foreach (var f in await Db.Context.Set<Field>().Where(f => fieldIds.Contains(f.Id)).ToListAsync()) f.ExcludeFromAi = true;
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();
    }

    private async Task<T> In<T>(RequestChannel channel, string user, Func<Task<T>> body)
    {
        As(user);
        _channel.Channel = channel;
        try { return await body(); }
        finally { _channel.Channel = RequestChannel.Ui; }
    }

    private Task<ServiceResult> Mcp(string user, MediatR.IRequest<ServiceResult> request) => In(RequestChannel.Mcp, user, () => Send(request));
    private Task<ServiceResult> Ui(string user, MediatR.IRequest<ServiceResult> request) => In(RequestChannel.Ui, user, () => Send(request));

    private Task<EffectivePermissions> Perms(RequestChannel channel, string user, string? module = null, Action<IServiceProvider>? before = null) =>
        In(channel, user, async () =>
        {
            using var scope = NewScope();
            var sp = scope.ServiceProvider;
            before?.Invoke(sp);
            var m = (await sp.GetRequiredService<IModuleRepository>().GetByKeyAsync(module ?? _r.Module, includeChildren: true, CancellationToken.None))!;
            return (await sp.GetRequiredService<IDynamicAccessGuard>().AuthorizeAsync(m, PermissionAction.Read, CancellationToken.None)).Permissions!;
        });

    private async Task<(long RecordId, long InstanceId)> Pending()
    {
        As("buyer1");
        var po = await NewPo(_r, qty: 2, price: 300, extra: V((_salary, 50000), (_bonus, 7)));
        return (po.Id, (await MustSubmit(po.Id, _r.Confirmed)).InstanceId!.Value);
    }

    private long[] AiDependents => [_salary, _salaryX, _priceSum, _countCond, _r.LineSum, _r.Total, _r.NetTotal];

    // ───────── guard ─────────

    [Fact]
    public async Task Guard_Mcp_HidesExcluded()
    {
        await Rig();
        var mcp = await Perms(RequestChannel.Mcp, "buyer1");
        var ui = await Perms(RequestChannel.Ui, "buyer1");

        mcp.HiddenFieldIds.Should().Contain(AiDependents).And.Contain(_bonus).And.NotContain(_countQty);
        ui.HiddenFieldIds.Should().BeEquivalentTo([_bonus]);
        ui.Hash.Should().NotBe(mcp.Hash);
    }

    [Fact]
    public async Task Guard_Mcp_SystemAdminAlsoHidden_OnlyAiHidden()
    {
        await Rig();
        var mcp = await Perms(RequestChannel.Mcp, "admin");
        var ui = await Perms(RequestChannel.Ui, "admin");
        var admin = PermissionResolver.ForSystemAdmin();

        // 獎金對 buyers 隱藏，但一般群組的欄位權限不套到管理員：只有 AI 排除欄位與依賴它的公式、彙總（判斷 10 第 3 點）
        mcp.HiddenFieldIds.Should().BeEquivalentTo(AiDependents);
        (mcp.Actions, mcp.Scope, mcp.OwnerUserIds).Should().BeEquivalentTo((admin.Actions, admin.Scope, admin.OwnerUserIds));
        mcp.ReadOnlyFieldIds.Should().BeEmpty();
        ui.HiddenFieldIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Guard_Mcp_FormulaClosure_AndRollups()
    {
        await Rig();
        var mcp = await Perms(RequestChannel.Mcp, "admin");
        // Q3：公式 salary * 1；Rollup_SumOfExcluded（加總對象是被遮的單價）；Rollup_SourceFormulaOfExcluded（加總對象是引用單價的小計）；
        // Rollup_CountWithConditionOnExcluded（沒有加總對象、條件引用單價）；條件只引用數量的計數照給
        mcp.HiddenFieldIds.Should().Contain([_salaryX, _priceSum, _r.LineSum, _countCond]).And.NotContain(_countQty);
        (await Perms(RequestChannel.Ui, "buyer1")).HiddenFieldIds.Should().NotContain(_countCond, "畫面通道不看「不提供給 AI」旗標；單價對 buyers 看得到");
    }

    [Fact]
    public async Task Rollup_ConditionUnparsable_Hidden()
    {
        await Rig();
        var field = await Db.Context.Set<Field>().SingleAsync(f => f.Id == _countQty);
        field.Config = field.Config with { Rollup = field.Config.Rollup! with { Condition = J("""{"bogus":1}""") } };
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();

        (await Perms(RequestChannel.Mcp, "admin")).HiddenFieldIds.Should().Contain(_countQty);
        (await Perms(RequestChannel.Ui, "admin")).HiddenFieldIds.Should().NotContain(_countQty);
    }

    /// <summary>第五輪審查必修 9：結構讀得懂、但正式 ConditionParser 會拒絕的條件（運算子不存在、欄位不在來源模組）→ MCP 通道隱藏。</summary>
    [Theory]
    [InlineData("op")]
    [InlineData("field")]
    public async Task Rollup_ConditionSemanticError_Hidden(string broken)
    {
        await Rig();
        var field = await Db.Context.Set<Field>().SingleAsync(f => f.Id == _countQty);
        var condition = broken == "op"
            ? $$"""{"field":{{_r.LineQty}},"op":"bogus","value":0}"""
            : $$"""{"field":{{_salary}},"op":"gt","value":0}""";
        field.Config = field.Config with { Rollup = field.Config.Rollup! with { Condition = J(condition) } };
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();

        (await Perms(RequestChannel.Mcp, "admin")).HiddenFieldIds.Should().Contain(_countQty);
        (await Perms(RequestChannel.Ui, "admin")).HiddenFieldIds.Should().NotContain(_countQty);
    }

    [Fact]
    public async Task Guard_Mcp_TitleTemplate()
    {
        await Rig();
        var module = await Db.Context.Set<Module>().SingleAsync(m => m.Key == _r.Module);
        module.TitleTemplate = $"{{{module.TitleFieldId}}} {{{_salary}}}";
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        var withFields = (await Svc<IModuleRepository>().GetByKeyAsync(_r.Module, includeChildren: true, CancellationToken.None))!;

        withFields.TitleVisibleTo(await Perms(RequestChannel.Mcp, "buyer1")).Should().BeFalse();
        withFields.TitleVisibleTo(await Perms(RequestChannel.Ui, "buyer1")).Should().BeTrue();
    }

    [Fact]
    public async Task Guard_SystemIdentity_NotAffected()
    {
        await Rig();
        IDisposable? entered = null;
        var p = await Perms(RequestChannel.Mcp, "buyer1", before: sp => entered = sp.GetRequiredService<ISystemIdentity>().Enter("buyer1"));
        entered?.Dispose();
        p.HiddenFieldIds.Should().BeEmpty("事件效果的系統身分不讀權限輸入（判斷 10 第 5 點）");
    }

    // ───────── 讀取路徑 ─────────

    [Fact]
    public async Task Filter_ByExcluded_Invalid()
    {
        await Rig();
        GetPagedRecordsRequest Filter(long field) => new(_r.Module, 1, 20, $$"""{"field":{{field}},"op":"gt","value":1}""", [], [], null);
        GetPagedRecordsRequest Sort(long field) => new(_r.Module, 1, 20, null, [$"f_{field}"], ["asc"], null);

        var mcp = await Mcp("buyer1", Filter(_salary));
        var hiddenByGroup = await Ui("buyer1", Filter(_bonus));
        mcp.IsInvalid.Should().BeTrue();
        hiddenByGroup.IsInvalid.Should().BeTrue();
        mcp.Errors.Keys.Should().BeEquivalentTo(hiddenByGroup.Errors.Keys, "與使用者沒有該欄位讀取權時同一個錯誤");
        mcp.Errors.Values.Single().Single().Should().Be(hiddenByGroup.Errors.Values.Single().Single().Replace($"{_bonus}", $"{_salary}"));
        (await Mcp("buyer1", Sort(_salary))).IsInvalid.Should().BeTrue();
        (await Ui("buyer1", Filter(_salary))).IsSuccess.Should().BeTrue();
        (await Ui("buyer1", Sort(_salary))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task History_HidesExcluded()
    {
        await Rig();
        As("buyer1");
        var po = await NewPo(_r, extra: V((_salary, 50000)));
        (await Send(new UpdateRecordRequest(po.Id, V((_salary, 60000)), Rec(po.Id).Row.RowVersion))).IsSuccess.Should().BeTrue();

        static List<string?> Keys(ServiceResult r) => ((PagedResponse<RecordHistoryResponse>)r.Data!).Items.Select(i => i.FieldKey).ToList();
        Keys(await Mcp("buyer1", new GetRecordHistoryRequest(po.Id, 1, 100, null))).Should().NotContain([$"f_{_salary}", $"f_{_salaryX}"]);
        Keys(await Ui("buyer1", new GetRecordHistoryRequest(po.Id, 1, 100, null))).Should().Contain($"f_{_salary}");
    }

    [Fact]
    public async Task Reference_TitleRestricted()
    {
        await Rig();
        var module = await Db.Context.Set<Module>().SingleAsync(m => m.Key == _r.Module);
        module.TitleTemplate = $"{{{module.TitleFieldId}}} {{{_salary}}}";
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        As("buyer1");
        var po = await NewPo(_r, extra: V((_salary, 50000)));
        var created = await Send(new CreateRecordRequest(_r.Extra, new Dictionary<string, JsonElement?>
        {
            [$"f_{TitleId(_r.Extra)}"] = JsonSerializer.SerializeToElement("運費"), [$"f_{_r.ExtraRef}"] = JsonSerializer.SerializeToElement(po.Id),
        }));
        created.IsCreated.Should().BeTrue(Dump(created));
        var extra = ((DynamicRecordResponse)created.Data!).Id;

        ReferenceInfo Ref(ServiceResult r) => ((DynamicRecordResponse)r.Data!).References[$"f_{_r.ExtraRef}"];
        var mcp = Ref(await Mcp("buyer1", new GetRecordRequest(extra)));
        (mcp.State, mcp.Title).Should().Be((ReferenceInfo.Restricted, null));
        Ref(await Ui("buyer1", new GetRecordRequest(extra))).State.Should().Be(ReferenceInfo.Ok);
    }

    [Fact]
    public async Task Export_Mcp_ExcludesColumn()
    {
        await Rig();
        As("buyer1");
        await NewPo(_r, extra: V((_salary, 50000)));
        static string Csv(ServiceResult r) => Encoding.UTF8.GetString(((ExportFile)r.Data!).Content);
        var request = new ExportRecordsRequest(_r.Module, "csv", null, [], [], null, "zh-TW");

        var mcp = Csv(await Mcp("admin", request));
        mcp.Should().NotContain("薪資").And.NotContain("50000");
        Csv(await Ui("admin", request)).Should().Contain("薪資").And.Contain("50000");
    }

    // ───────── 簽核路徑 ─────────

    [Fact]
    public async Task ApprovalGrant_Mcp_HidesExcluded()
    {
        await Rig();
        var (po, _) = await Pending();

        var mcp = (DynamicRecordResponse)(await Mcp("fin1", new GetRecordRequest(po))).Data!;
        var ui = (DynamicRecordResponse)(await Ui("fin1", new GetRecordRequest(po))).Data!;

        mcp.Values.Keys.Should().NotContain([$"f_{_salary}", $"f_{_salaryX}", $"f_{_r.LineSum}", $"f_{_r.Total}"]);
        ui.Values[$"f_{_salary}"]!.Value.GetDecimal().Should().Be(50000);
        ui.Values.Keys.Should().Contain($"f_{_salaryX}");
    }

    [Fact]
    public async Task ApprovalGrant_Detail_Mcp_HidesExcluded()
    {
        await Rig();
        var (po, _) = await Pending();

        var mcp = ((DynamicRecordResponse)(await Mcp("fin1", new GetRecordRequest(po))).Data!).Details![_r.Line].Single();
        var ui = ((DynamicRecordResponse)(await Ui("fin1", new GetRecordRequest(po))).Data!).Details![_r.Line].Single();

        mcp.Values.Keys.Should().NotContain([$"f_{_r.LinePrice}", $"f_{_r.LineSubtotal}"]).And.Contain($"f_{_r.LineQty}");
        ui.Values.Keys.Should().Contain([$"f_{_r.LinePrice}", $"f_{_r.LineSubtotal}"]);
    }

    /// <summary>
    /// 同意、加簽時改過的欄位鍵依讀者欄位權限遮蔽（計畫判斷 10 列的例外：畫面通道也照讀者欄位權限遮）。三種對照：
    /// 畫面通道的一般隱藏欄位（獎金對 buyers 隱藏）不出現；只標「不提供給 AI」的欄位（薪資）在畫面通道照常出現；MCP 通道遮掉薪資。
    /// </summary>
    [Theory]
    [InlineData("approve")]
    [InlineData("addSign")]
    public async Task ApprovalHistory_EditedKeys_MaskedByReader(string action)
    {
        await Rig();
        var (po, id) = await Pending();
        As("fin1");
        var task = TasksOf(id).Single(t => t.AssigneeUserId == "fin1");
        var values = V((_salary, 48000), (_bonus, 9));
        var done = action == "approve"
            ? await Send(new ApproveTaskRequest(task.Id, Inst(id).RowVersion, null, values))
            : await Send(new AddSignTaskRequest(task.Id, Inst(id).RowVersion, null, values, ["emp1"], ApprovalModes.Any));
        done.IsSuccess.Should().BeTrue(Dump(done));

        static string Data(ServiceResult r)
        {
            var view = (RecordApprovalResponse)r.Data!;
            var all = (view.Current is null ? view.Previous : [view.Current, .. view.Previous]).SelectMany(i => i.Actions);
            return string.Join("\n", all.Select(a => a.Data?.GetRawText() ?? ""));
        }
        string salary = $"\"f_{_salary}\"", bonus = $"\"f_{_bonus}\"";   // 改過的欄位記在動作的 Data（鍵清單）
        Data(await Ui("fin1", new GetRecordApprovalRequest(po))).Should().Contain(salary).And.Contain(bonus, "對照：寫進歷程的鍵兩個都有");
        var ui = Data(await Ui("buyer1", new GetRecordApprovalRequest(po)));
        ui.Should().NotContain(bonus, "畫面通道的一般隱藏欄位照讀者權限遮");
        ui.Should().Contain(salary, "只標不提供給 AI 的欄位在畫面通道照常出現");
        Data(await Mcp("buyer1", new GetRecordApprovalRequest(po))).Should().NotContain(salary).And.NotContain(bonus);
        Data(await Mcp("admin", new GetRecordApprovalRequest(po))).Should().NotContain(salary);
    }

    // ───────── 一般參照來源的彙總（第五輪審查必修 7）─────────

    private long _secret, _refSum, _refCount, _refSumX;

    /// <summary>附加費用（一般參照指回採購單）加「機密」欄位並標不提供給 AI；採購單以它加總、以它為條件計數，另有引用加總的公式。</summary>
    private async Task RigRegularSource()
    {
        await Rig();
        _secret = (await MustAdd(_r.Extra, FieldType.Decimal, new FieldConfig { IsFilterable = true, Scale = 0 }, "機密")).Id;
        _refSum = (await MustAdd(_r.Module, FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(_r.ExtraRef, RollupFunction.Sum, _secret, null) }, "機密合計")).Id;
        _refCount = (await MustAdd(_r.Module, FieldType.Rollup, new FieldConfig
        {
            Rollup = new RollupConfig(_r.ExtraRef, RollupFunction.Count, null, J($$"""{"field":{{_secret}},"op":"gt","value":0}"""))
        }, "機密筆數")).Id;
        _refSumX = (await MustAdd(_r.Module, FieldType.Formula,
            new FieldConfig { Formula = $"{{{_refSum}}} * 1", ResultType = "number", Scale = 0 }, "機密合計公式")).Id;
        await Exclude(_secret);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("buyer1")]
    public async Task Rollup_RegularRefSource_SumOfExcluded_Hidden(string user)
    {
        await RigRegularSource();
        (await Perms(RequestChannel.Mcp, user)).HiddenFieldIds.Should().Contain(_refSum);
        (await Perms(RequestChannel.Ui, user)).HiddenFieldIds.Should().NotContain(_refSum, "畫面通道不看「不提供給 AI」旗標；機密對 buyers 看得到");
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("buyer1")]
    public async Task Rollup_RegularRefSource_CountWithConditionOnExcluded_Hidden(string user)
    {
        await RigRegularSource();
        (await Perms(RequestChannel.Mcp, user)).HiddenFieldIds.Should().Contain(_refCount);
        (await Perms(RequestChannel.Ui, user)).HiddenFieldIds.Should().NotContain(_refCount);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("buyer1")]
    public async Task Rollup_RegularRefSource_FormulaOfRollup_Hidden(string user)
    {
        await RigRegularSource();
        (await Perms(RequestChannel.Mcp, user)).HiddenFieldIds.Should().Contain(_refSumX);
        (await Perms(RequestChannel.Ui, user)).HiddenFieldIds.Should().NotContain(_refSumX);
    }

    [Fact]
    public async Task ApprovalGrant_RegularRefSource_Hidden()
    {
        await RigRegularSource();
        As("buyer1");
        var po = await NewPo(_r, qty: 2, price: 300, extra: V((_salary, 50000)));
        var created = await Send(new CreateRecordRequest(_r.Extra, new Dictionary<string, JsonElement?>
        {
            [$"f_{TitleId(_r.Extra)}"] = JsonSerializer.SerializeToElement("運費"), [$"f_{_r.ExtraRef}"] = JsonSerializer.SerializeToElement(po.Id),
            [$"f_{_secret}"] = JsonSerializer.SerializeToElement(1234),
        }));
        created.IsCreated.Should().BeTrue(Dump(created));
        await MustSubmit(po.Id, _r.Confirmed);

        var keys = new[] { $"f_{_refSum}", $"f_{_refCount}", $"f_{_refSumX}" };
        var ui = (DynamicRecordResponse)(await Ui("fin1", new GetRecordRequest(po.Id))).Data!;
        ui.ApprovalGrant.Should().BeTrue("fin1 只靠簽核的臨時讀取權讀這筆");
        ui.Values[$"f_{_refSum}"]!.Value.GetDecimal().Should().Be(1234, "對照：來源只有一筆時 SUM 等於原值");
        var mcp = (DynamicRecordResponse)(await Mcp("fin1", new GetRecordRequest(po.Id))).Data!;
        mcp.ApprovalGrant.Should().BeTrue();
        mcp.Values.Keys.Should().NotContain(keys);
    }

    // ───────── 停用的公式（第五輪審查必修 8）─────────

    /// <summary>公式「薪資 × 1」先存值再停用（直接改資料庫，測試許可）。</summary>
    private async Task<long> DisableSalaryFormula()
    {
        var field = await Db.Context.Set<Field>().SingleAsync(f => f.Id == _salaryX);
        field.Status = FieldStatus.Inactive;
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();
        return _salaryX;
    }

    [Fact]
    public async Task DisabledFormula_Record_Hidden()
    {
        await Rig();
        As("buyer1");
        var po = await NewPo(_r, extra: V((_salary, 50000)));
        var formula = await DisableSalaryFormula();

        var ui = (DynamicRecordResponse)(await Ui("buyer1", new GetRecordRequest(po.Id))).Data!;
        ui.Values[$"f_{formula}"]!.Value.GetDecimal().Should().Be(50000, "對照：停用的公式存過的值仍讀得到");
        ((DynamicRecordResponse)(await Mcp("buyer1", new GetRecordRequest(po.Id))).Data!).Values.Keys.Should().NotContain($"f_{formula}");
        ((DynamicRecordResponse)(await Mcp("admin", new GetRecordRequest(po.Id))).Data!).Values.Keys.Should().NotContain($"f_{formula}");
    }

    /// <summary>
    /// 多層停用公式鏈、欄位順序反向：A＝基數 × 1、B＝A × 1、C＝B × 1，三者存值後都停用；建立順序是 C、B、A（id 與 Sequence 都反過來），
    /// 單趟依序掃只會遮到 A、B。A 標「不提供給 AI」後 B、C 都要被遮，鎖住閉包的反覆掃描。
    /// </summary>
    [Fact]
    public async Task DisabledFormulaChain_ReversedOrder_AllHidden()
    {
        await Rig();
        var basis = (await MustAdd(_r.Module, FieldType.Decimal, new FieldConfig { Scale = 0 }, "基數")).Id;
        static FieldConfig Formula(string expr) => new() { Formula = expr, ResultType = "number", Scale = 0 };
        var c = await MustAdd(_r.Module, FieldType.Formula, Formula("0"), "公式C");
        var b = await MustAdd(_r.Module, FieldType.Formula, Formula("0"), "公式B");
        var a = await MustAdd(_r.Module, FieldType.Formula, Formula("0"), "公式A");
        (await Update(a, Formula($"{{{basis}}} * 1"))).IsSuccess.Should().BeTrue();
        (await Update(b, Formula($"{{{a.Id}}} * 1"))).IsSuccess.Should().BeTrue();
        (await Update(c, Formula($"{{{b.Id}}} * 1"))).IsSuccess.Should().BeTrue();
        As("buyer1");
        var po = await NewPo(_r, extra: V((basis, 500)));
        var ids = new[] { a.Id, b.Id, c.Id };
        var fields = await Db.Context.Set<Field>().Where(f => ids.Contains(f.Id)).ToListAsync();
        foreach (var f in fields)
        {
            f.Status = FieldStatus.Inactive;
            f.Sequence = f.Id == c.Id ? -3 : f.Id == b.Id ? -2 : -1;
        }
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();
        var keys = ids.Select(id => $"f_{id}").ToArray();

        ((DynamicRecordResponse)(await Mcp("buyer1", new GetRecordRequest(po.Id))).Data!).Values.Keys
            .Should().Contain(keys, "對照：還沒標旗標時，停用的公式在 MCP 照樣讀得到");
        await Exclude(a.Id);

        var ui = (DynamicRecordResponse)(await Ui("buyer1", new GetRecordRequest(po.Id))).Data!;
        ui.Values[$"f_{c.Id}"]!.Value.GetDecimal().Should().Be(500, "對照：畫面通道照樣讀得到存過的值");
        ((DynamicRecordResponse)(await Mcp("buyer1", new GetRecordRequest(po.Id))).Data!).Values.Keys.Should().NotContain(keys);
        ((DynamicRecordResponse)(await Mcp("admin", new GetRecordRequest(po.Id))).Data!).Values.Keys.Should().NotContain(keys);
    }

    /// <summary>公式本來不記歷程（RecordHistoryWriter 略過計算欄位）；這裡直接寫一列，鎖住歷程查詢套用同一個遮罩（舊資料或日後改寫入規則時不外洩）。</summary>
    [Fact]
    public async Task DisabledFormula_History_Hidden()
    {
        await Rig();
        As("buyer1");
        var po = await NewPo(_r, extra: V((_salary, 50000)));
        var formula = await DisableSalaryFormula();
        Db.Context.Add(new RecordHistory
        {
            RecordId = po.Id, FieldId = formula, Operation = RecordHistoryOperation.Update, OldValue = "40000", NewValue = "50000",
            ChangedUserId = "buyer1", ChangedTime = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Unspecified),
        });
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();

        static List<string?> Keys(ServiceResult r) => ((PagedResponse<RecordHistoryResponse>)r.Data!).Items.Select(i => i.FieldKey).ToList();
        Keys(await Ui("buyer1", new GetRecordHistoryRequest(po.Id, 1, 100, null))).Should().Contain($"f_{formula}", "對照：畫面通道讀得到這一列");
        Keys(await Mcp("buyer1", new GetRecordHistoryRequest(po.Id, 1, 100, null))).Should().NotContain($"f_{formula}");
        Keys(await Mcp("admin", new GetRecordHistoryRequest(po.Id, 1, 100, null))).Should().NotContain($"f_{formula}");
    }

    [Fact]
    public async Task ApprovalTitleSnapshot_Mcp_Blank()
    {
        await Rig(flag: false);
        var (po, _) = await Pending();

        async Task<List<string>> Titles(RequestChannel channel)
        {
            var list = new List<string>();
            foreach (var (user, box) in new[] { ("fin1", "todo"), ("buyer1", "submitted"), ("admin", "all") })
            {
                var r = await In(channel, user, () => Send(new GetApprovalTasksRequest(box, null, null, null, 1, 20)));
                list.Add(((PagedResponse<ApprovalInboxItem>)r.Data!).Items.Single().TitleSnapshot);
            }
            foreach (var user in new[] { "buyer1", "fin1", "admin" })
            {
                var r = await In(channel, user, () => Send(new GetRecordApprovalRequest(po)));
                list.Add(((RecordApprovalResponse)r.Data!).Current!.TitleSnapshot);
            }
            return list;
        }

        (await Titles(RequestChannel.Mcp)).Should().AllBe("單據", "模組沒有被遮的欄位時照給");
        await Exclude(_salary);
        (await Titles(RequestChannel.Mcp)).Should().AllBe("", "Q7：發起人、簽核人、簽核管理者一律空字串");
        (await Titles(RequestChannel.Ui)).Should().AllBe("單據");
    }
}
