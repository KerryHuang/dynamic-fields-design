using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 內建往來對象被客戶改過之後的匯出與再匯入（共用主檔設計 §9、§15.2「匯出匯入」）：
/// 自己匯入自己不產生重複；搬到新的資料庫時，匯入的主檔優先於內建的，開啟 CRM 得到改過的那份。
/// </summary>
public class BuiltInMasterRoundTripTests
{
    private const string Partner = BuiltInMasterTests.Partner;

    private static async Task<long> OpenCrmAndAddABaseFieldAsync(SharedMasterRig rig)
    {
        await rig.OpenOkAsync("crm-sales", CrmMasterTests.CrmBody);
        return await rig.AddDesignerFieldAsync(Partner, rig.BlockId("partner", "partner.main"), "text", "官網");
    }

    private static List<string> BaseFieldCodes(System.Text.Json.JsonElement package) =>
        package.GetProperty("modules").EnumerateArray().Single().GetProperty("blocks").EnumerateArray()
            .SelectMany(b => b.GetProperty("fields").EnumerateArray()).Select(f => f.GetProperty("code").GetString()!).ToList();

    private static int PartnerFieldCount(SharedMasterRig rig) => rig.Blocks(Partner).Sum(b => b.Fields.Count);

    [Fact]
    public async Task AModifiedBuiltInPartner_ReimportedIntoItsOwnDatabase_MergesWithoutDuplicates()
    {
        using var rig = new SharedMasterRig();
        await OpenCrmAndAddABaseFieldAsync(rig);
        var (package, _) = await rig.ExportAsync("partner");
        var codes = BaseFieldCodes(package);
        codes.Should().Contain(c => c.StartsWith('u'), "設計器加的基本欄位有持久代號，隨主檔匯出");
        codes.Should().NotContain(["industry", "fromLead"], "主檔匯出不含 CRM 的貢獻");
        var before = rig.Blocks(Partner);

        await rig.ImportMergeOkAsync(package.GetRawText());

        rig.Blocks(Partner).Should().BeEquivalentTo(before, o => o.WithStrictOrdering(), "自己匯入自己不多出欄位或區塊");
        rig.Db(db => db.Set<App>().Count(a => a.Key == "partner")).Should().Be(1);
        await rig.AssertIntegrityAsync();
    }

    [Fact]
    public async Task InANewDatabase_TheImportedPartnerTakesPriorityOverTheBuiltIn()
    {
        System.Text.Json.JsonElement exported;
        List<(string Block, List<string> Fields)> original;
        using (var source = new SharedMasterRig())
        {
            await OpenCrmAndAddABaseFieldAsync(source);
            exported = (await source.ExportAsync("partner")).Package.Clone();
            original = source.Blocks(Partner);
        }

        using var rig = new SharedMasterRig();
        await rig.ImportMergeOkAsync(exported.GetRawText()); // 同 id 的內建主檔存在：合併成一份匯入的定義（source = imported）
        (await ApiTestKit.JsonOf(await rig.Admin.GetAsync($"{SharedMasterRig.Apps}/packages"))).EnumerateArray()
            .Select(p => p.GetProperty("id").GetString()).Should().NotContain("partner", "主檔仍不在可開啟清單");
        await rig.OpenOkAsync("crm-sales", CrmMasterTests.CrmBody);

        rig.Blocks(Partner).Should().BeEquivalentTo(original, o => o.WithStrictOrdering(), "開啟 CRM 用的是匯入的那份往來對象");
        rig.Blocks(Partner)[0].Fields.Should().ContainSingle(f => f.StartsWith("官網:"));
        rig.Db(db => db.Set<App>().Count(a => a.Key == "partner")).Should().Be(1);
        PartnerFieldCount(rig).Should().Be(original.Sum(b => b.Fields.Count));
        BaseFieldCodes((await rig.ExportAsync("partner")).Package).Should().Equal(BaseFieldCodes(exported), "再匯出的代號與原檔相同");
        await rig.AssertIntegrityAsync();
    }
}
