using System.Text;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.ExportService.Queries.ExportRecords;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetPagedRecords;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.ApplicationTests.Dynamic.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>
/// 畫面通道的彙總與公式遮罩（#74、#75；使用者 2026-11-21 裁決：比照 MCP 通道）。讀者看不到的欄位，不能經由
/// 一般參照來源的彙總、以它為條件的彙總、或引用它的停用公式帶出值；系統管理員照常看得到。
/// 情境：附加費用（一般參照指回採購單）的「機密」與單據的「獎金」對 buyers、finance 隱藏，明細「單價」對 buyers 隱藏。
/// 採購單另加：機密合計（加總機密）、機密筆數（條件是機密 &gt; 0）、機密合計公式、高價筆數（明細來源、條件是單價 &gt; 100）、
/// 數量筆數（條件是數量，對照組）、獎金公式（獎金 × 1，存值後停用）。
/// </summary>
[Trait("Category", "Postgres")]
public class RollupFormulaMaskTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private PurchaseRig _r = null!;
    private long _secret, _refSum, _refCount, _refSumX, _priceCount, _qtyCount, _bonus, _bonusX, _buyers;

    private async Task Rig()
    {
        _r = await BuildPurchase();
        _secret = (await MustAdd(_r.Extra, FieldType.Decimal, new FieldConfig { IsFilterable = true, Scale = 0 }, "機密")).Id;
        _refSum = (await MustAdd(_r.Module, FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(_r.ExtraRef, RollupFunction.Sum, _secret, null) }, "機密合計")).Id;
        _refCount = (await MustAdd(_r.Module, FieldType.Rollup, new FieldConfig
        {
            Rollup = new RollupConfig(_r.ExtraRef, RollupFunction.Count, null, J($$"""{"field":{{_secret}},"op":"gt","value":0}"""))
        }, "機密筆數")).Id;
        _refSumX = (await MustAdd(_r.Module, FieldType.Formula,
            new FieldConfig { Formula = $"{{{_refSum}}} * 1", ResultType = "number", Scale = 0 }, "機密合計公式")).Id;
        _priceCount = (await MustAdd(_r.Module, FieldType.Rollup, new FieldConfig
        {
            Rollup = new RollupConfig(_r.LineHead, RollupFunction.Count, null, J($$"""{"field":{{_r.LinePrice}},"op":"gt","value":100}"""))
        }, "高價筆數")).Id;
        _qtyCount = (await MustAdd(_r.Module, FieldType.Rollup, new FieldConfig
        {
            Rollup = new RollupConfig(_r.LineHead, RollupFunction.Count, null, J($$"""{"field":{{_r.LineQty}},"op":"gt","value":0}"""))
        }, "數量筆數")).Id;
        _bonus = (await MustAdd(_r.Module, FieldType.Decimal, new FieldConfig { Scale = 0 }, "獎金")).Id;
        _bonusX = (await MustAdd(_r.Module, FieldType.Formula,
            new FieldConfig { Formula = $"{{{_bonus}}} * 1", ResultType = "number", Scale = 0 }, "獎金公式")).Id;
        await SeedFlow(_r, "po-g", _r.Confirmed, 1, Def(null, Stage("fin", Group(FinanceGroup))));
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
        foreach (var (m, field) in new[] { (_r.Module, _bonus), (_r.Extra, _secret) })
        {
            var res = await Send(new SaveModulePermissionsRequest(m, new ModulePermissionsDto(
                [new GroupPermissionDto(_buyers, new PrivilegesDto(true, true, true, true, true, true, false), DataScope.All)], [],
                [new FieldPermissionDto(field, _buyers, FieldAccess.Hidden), new FieldPermissionDto(field, FinanceGroup, FieldAccess.Hidden)], [])));
            res.IsSuccess.Should().BeTrue(Dump(res));
        }
        await HideDetailFor(_r.Line, _r.LinePrice, _buyers);
    }

    private async Task<EffectivePermissions> Perms(string user)
    {
        As(user);
        using var scope = NewScope();
        var sp = scope.ServiceProvider;
        var m = (await sp.GetRequiredService<IModuleRepository>().GetByKeyAsync(_r.Module, includeChildren: true, CancellationToken.None))!;
        return (await sp.GetRequiredService<IDynamicAccessGuard>().AuthorizeAsync(m, PermissionAction.Read, CancellationToken.None)).Permissions!;
    }

    /// <summary>admin 建一張單據（明細單價 300）與一筆附加費用（機密 1234），獎金 7；回傳單據 Id。</summary>
    private async Task<long> PoWithExtra()
    {
        As("admin");
        var po = await NewPo(_r, qty: 2, price: 300, extra: V((_bonus, 7)));
        var created = await Send(new CreateRecordRequest(_r.Extra, new Dictionary<string, JsonElement?>
        {
            [$"f_{TitleId(_r.Extra)}"] = JsonSerializer.SerializeToElement("運費"), [$"f_{_r.ExtraRef}"] = JsonSerializer.SerializeToElement(po.Id),
            [$"f_{_secret}"] = JsonSerializer.SerializeToElement(1234),
        }));
        created.IsCreated.Should().BeTrue(Dump(created));
        return po.Id;
    }

    private async Task<DynamicRecordResponse> Get(string user, long id)
    {
        As(user);
        var res = await Send(new GetRecordRequest(id));
        res.IsSuccess.Should().BeTrue(Dump(res));
        return (DynamicRecordResponse)res.Data!;
    }

    private string[] RefKeys => [$"f_{_refSum}", $"f_{_refCount}", $"f_{_refSumX}"];

    // ───────── #74 第 1 點：一般參照來源的彙總 ─────────

    [Fact]
    public async Task Guard_RegularRefSource_SumOfHidden_HiddenForReader_VisibleForAdmin()
    {
        await Rig();
        (await Perms("buyer1")).HiddenFieldIds.Should().Contain([_refSum, _refSumX]);
        (await Perms("admin")).HiddenFieldIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Record_RegularRefSource_SumOfHidden_NotReturned()
    {
        await Rig();
        var po = await PoWithExtra();

        var admin = await Get("admin", po);
        admin.Values[$"f_{_refSum}"]!.Value.GetDecimal().Should().Be(1234, "對照：來源只有一筆時 SUM 等於原值");
        admin.Values[$"f_{_refSumX}"]!.Value.GetDecimal().Should().Be(1234);
        (await Get("buyer1", po)).Values.Keys.Should().NotContain([$"f_{_refSum}", $"f_{_refSumX}"]);
    }

    [Fact]
    public async Task List_RegularRefSource_SumOfHidden_NotReturned()
    {
        await Rig();
        await PoWithExtra();

        async Task<DynamicRecordResponse> Row(string user)
        {
            As(user);
            var res = await Send(new GetPagedRecordsRequest(_r.Module, 1, 20, null, [], [], null));
            res.IsSuccess.Should().BeTrue(Dump(res));
            return ((PagedResponse<DynamicRecordResponse>)res.Data!).Items.Single();
        }
        (await Row("admin")).Values.Keys.Should().Contain(RefKeys);
        (await Row("buyer1")).Values.Keys.Should().NotContain(RefKeys);
    }

    [Fact]
    public async Task Export_RegularRefSource_SumOfHidden_ColumnExcluded()
    {
        await Rig();
        await PoWithExtra();
        async Task<string> Csv(string user)
        {
            As(user);
            var res = await Send(new ExportRecordsRequest(_r.Module, "csv", null, [], [], null, "zh-TW"));
            res.IsSuccess.Should().BeTrue(Dump(res));
            return Encoding.UTF8.GetString(((ExportFile)res.Data!).Content);
        }

        (await Csv("admin")).Should().Contain("機密合計").And.Contain("機密筆數").And.Contain("1234");
        (await Csv("buyer1")).Should().NotContain("機密合計").And.NotContain("機密筆數").And.NotContain("1234");
    }

    // ───────── #75：條件引用看不到欄位的彙總 ─────────

    [Fact]
    public async Task Guard_ConditionOnHidden_HiddenForReader_VisibleForAdmin()
    {
        await Rig();
        var buyer = await Perms("buyer1");
        buyer.HiddenFieldIds.Should().Contain(_refCount, "一般參照來源、條件是看不到的機密")
            .And.Contain(_priceCount, "明細來源、條件是看不到的單價")
            .And.NotContain(_qtyCount, "條件只引用看得到的數量");
        (await Perms("admin")).HiddenFieldIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Record_ConditionOnHidden_NotReturned()
    {
        await Rig();
        var po = await PoWithExtra();

        var admin = await Get("admin", po);
        admin.Values[$"f_{_refCount}"]!.Value.GetDecimal().Should().Be(1);
        admin.Values[$"f_{_priceCount}"]!.Value.GetDecimal().Should().Be(1);
        var buyer = await Get("buyer1", po);
        buyer.Values.Keys.Should().NotContain([$"f_{_refCount}", $"f_{_priceCount}"]).And.Contain($"f_{_qtyCount}");
    }

    /// <summary>條件解析失敗（運算子不存在）時 RollupCalculator 會略過條件算全部來源列，讀者端一律隱藏；管理員照常。</summary>
    [Fact]
    public async Task Guard_ConditionUnparsable_HiddenForReader()
    {
        await Rig();
        var field = await Db.Context.Set<Field>().SingleAsync(f => f.Id == _qtyCount);
        field.Config = field.Config with { Rollup = field.Config.Rollup! with { Condition = J($$"""{"field":{{_r.LineQty}},"op":"bogus","value":0}""") } };
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();

        (await Perms("buyer1")).HiddenFieldIds.Should().Contain(_qtyCount);
        (await Perms("admin")).HiddenFieldIds.Should().BeEmpty();
    }

    // ───────── #74 第 2 點：引用隱藏欄位的停用公式 ─────────

    private async Task DisableBonusFormula()
    {
        var field = await Db.Context.Set<Field>().SingleAsync(f => f.Id == _bonusX);
        field.Status = FieldStatus.Inactive;
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();
    }

    [Fact]
    public async Task DisabledFormula_RefsHidden_NotReturned()
    {
        await Rig();
        var po = await PoWithExtra();
        await DisableBonusFormula();

        (await Get("admin", po)).Values[$"f_{_bonusX}"]!.Value.GetDecimal().Should().Be(7, "對照：停用的公式存過的值仍讀得到");
        (await Perms("buyer1")).HiddenFieldIds.Should().Contain(_bonusX);
        (await Get("buyer1", po)).Values.Keys.Should().NotContain($"f_{_bonusX}");
    }

    // ───────── 簽核臨時讀取權 ─────────

    [Fact]
    public async Task ApprovalGrant_RegularRefSourceAndDisabledFormula_Hidden()
    {
        await Rig();
        var po = await PoWithExtra();
        await DisableBonusFormula();
        As("admin");
        await MustSubmit(po, _r.Confirmed);

        var fin = await Get("fin1", po);
        fin.ApprovalGrant.Should().BeTrue("fin1 只靠簽核的臨時讀取權讀這筆");
        fin.Values.Keys.Should().NotContain([.. RefKeys, $"f_{_bonusX}"]);
        (await Get("admin", po)).Values.Keys.Should().Contain([.. RefKeys, $"f_{_bonusX}"]);
    }
}
