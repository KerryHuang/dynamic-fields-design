using System.Text.Json;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.PermissionAxes;

/// <summary>
/// 帶表頭的值依呼叫端對表頭的權限（M6，PAX-14 的單元部分；權限軸設計 §12.2）：<see cref="DetailWriter.PrepareAsync"/> 用傳入的
/// <b>表頭</b>權限過濾表頭值，不用迴圈裡的明細權限。呼叫端是 admin，明細權限沒有隱藏任何欄位。
/// </summary>
public sealed class PermissionAxesDetailWriterTests : DesignerTestBase
{
    private const string H = "pax-h", D = "pax-d";

    private static EffectivePermissions Header(params long[] hidden) => new(
        Enum.GetValues<PermissionAction>().ToHashSet(), DataScope.All, null, hidden.ToHashSet(), new HashSet<long>(), new HashSet<long>(), "test");

    private async Task<(long Salary, long Price, long X, long Y, string LineTitle)> SetUpAsync()
    {
        await NewModule(H, activate: true);
        var salary = await MustAdd(H, FieldType.Decimal, label: "薪資");
        var price = await MustAdd(H, FieldType.Currency, label: "單價");
        await NewModule(D);
        await MustAdd(D, FieldType.Reference, RefTo(H, ReferenceRelation.Detail), "表頭");
        var x = await MustAdd(D, FieldType.Decimal, new FieldConfig { ParentDefaultFieldId = salary.Id }, "X");
        var y = await MustAdd(D, FieldType.Currency, new FieldConfig { ParentDefaultFieldId = price.Id }, "Y");
        (await Send(new DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule.ActivateModuleRequest(D))).IsSuccess.Should().BeTrue();
        return (salary.Id, price.Id, x.Id, y.Id, $"f_{(await Detail(D)).TitleFieldId}");
    }

    private async Task<Dictionary<string, JsonElement?>> PrepareAsync(string lineTitle, EffectivePermissions header, long salary, long price)
    {
        var parent = (await Svc<IModuleRepository>().GetByKeyAsync(H, includeChildren: true, CancellationToken.None))!;
        var details = new Dictionary<string, List<DetailRowInput>>
        {
            [D] = [new DetailRowInput(null, new Dictionary<string, JsonElement?> { [lineTitle] = J("\"L\"") }, null)],
        };
        IReadOnlyDictionary<string, JsonElement?> values = new Dictionary<string, JsonElement?> { [$"f_{salary}"] = J("100"), [$"f_{price}"] = J("50") };
        var plan = await Svc<DetailWriter>().PrepareAsync(parent, null, PermissionAction.Add, details, header, CancellationToken.None,
            () => Task.FromResult(values));
        plan.Failure.Should().BeNull();
        plan.Errors.Should().BeEmpty();
        return plan.Sets.Single().Changes.Single().After!;
    }

    private static decimal? Num(Dictionary<string, JsonElement?> row, long id) =>
        row.GetValueOrDefault($"f_{id}") is { ValueKind: JsonValueKind.Number } v ? v.GetDecimal() : null;

    [Fact]
    public async Task PAX14_HeaderValuesAreFilteredByHeaderPermissions_NotDetailPermissions()
    {
        var (salary, price, x, y, title) = await SetUpAsync();

        var hidden = await PrepareAsync(title, Header(salary), salary, price);
        Num(hidden, x).Should().BeNull("表頭權限隱藏薪資；明細權限（admin）沒有隱藏任何欄位，結果仍是空值");
        Num(hidden, y).Should().Be(50m);

        var visible = await PrepareAsync(title, Header(), salary, price);
        (Num(visible, x), Num(visible, y)).Should().Be((100m, 50m));
    }
}
