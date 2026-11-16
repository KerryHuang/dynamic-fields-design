using System.Text;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ExportService;
using DynamicModules.Application.Dynamic.ExportService.Queries.ExportRecords;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecordHistory;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>員工離職（停用帳號）後，舊記錄的編輯、歷程與匯出不能壞。</summary>
public class DeactivatedUserTests : DesignerTestBase
{
    private static Dictionary<string, JsonElement?> Vals(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => (JsonElement?)JsonSerializer.SerializeToElement(p.Value));

    private async Task<(string Title, string Who, long RecordId)> RecordOwnedBy(string userId)
    {
        await NewModule("memo");
        var who = await MustAdd("memo", FieldType.User, label: "處理人");
        (await Send(new ActivateModuleRequest("memo"))).IsSuccess.Should().BeTrue();
        var title = $"f_{(await Detail("memo")).TitleFieldId}";
        var created = await Send(new CreateRecordRequest("memo", Vals((title, "a"), (who.Key, userId))));
        created.IsCreated.Should().BeTrue(created.Message);
        return (title, who.Key, ((DynamicRecordResponse)created.Data!).Id);
    }

    private async Task Deactivate(string userId)
    {
        (await Db.Context.Set<AppUser>().SingleAsync(u => u.UserId == userId)).IsActive = false;
        await Db.Context.SaveChangesAsync();
    }

    [Fact]
    public async Task EditRecord_WithInactiveUserFieldValue_Succeeds()
    {
        var (title, who, id) = await RecordOwnedBy("sales01");
        await Deactivate("sales01");
        var version = await Db.Context.Set<Record>().AsNoTracking().Where(r => r.Id == id).Select(r => r.RowVersion).SingleAsync();

        var r = await Send(new UpdateRecordRequest(id, Vals((title, "b"), (who, "sales01")), version, null));

        r.IsSuccess.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
    }

    [Fact]
    public async Task History_ShowsInactiveUserName()
    {
        var (_, who, id) = await RecordOwnedBy("sales01");
        await Deactivate("sales01");
        var version = await Db.Context.Set<Record>().AsNoTracking().Where(r => r.Id == id).Select(r => r.RowVersion).SingleAsync();
        (await Send(new UpdateRecordRequest(id, Vals((who, "sales02")), version, null))).IsSuccess.Should().BeTrue();

        var page = (PagedResponse<RecordHistoryResponse>)(await Send(new GetRecordHistoryRequest(id, 1, 20, "zh-TW"))).Data!;

        page.Items.First(i => i.FieldKey == who).OldText.Should().Be("業務一");
    }

    [Fact]
    public async Task Export_ShowsInactiveUserName()
    {
        await RecordOwnedBy("sales01");
        await Deactivate("sales01");

        var file = (ExportFile)(await Send(new ExportRecordsRequest("memo", "csv", null, [], [], null, "zh-TW"))).Data!;

        Encoding.UTF8.GetString(file.Content).Should().Contain("業務一");
    }
}
