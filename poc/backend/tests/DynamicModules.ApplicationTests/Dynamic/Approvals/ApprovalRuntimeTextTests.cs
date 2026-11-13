using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Organization;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>簽核執行期訊息的字串表：繁中與搬進來之前逐字相同，其餘語系都有文字；簽核人解析的錯誤依語系帶關卡與來源名稱。</summary>
public class ApprovalRuntimeTextTests
{
    public static TheoryData<string, string, object[]> ZhLiterals => new()
    {
        { MessageText.ApprovalTooManyApprovers, "關卡「S」的簽核人超過 20 人", ["S", 20] },
        { MessageText.ApprovalFlowUndecidable, "簽核流程「F」的條件無法判斷，請系統管理員檢查流程設定", ["F"] },
        { MessageText.ApprovalNoApprover, "關卡「S」的來源「X」找不到簽核人，備援也沒有人", ["S", "X"] },
        { MessageText.ApprovalManagerLabel, "第 2 層主管", [2] },
        { MessageText.ApprovalPickLabel, "發起人自選", [] },
        { MessageText.ApprovalNeedsSubmit, "這個狀態需要送出簽核", [] },
        { MessageText.ApprovalPending, "簽核中，不可修改", [] },
        { MessageText.ApprovalWithdrawFirst, "簽核中，請先撤回", [] },
        { MessageText.ApprovalEventBlocked, "記錄 7 正在簽核中，這次請求改動了它，已取消", [7L] },
        { MessageText.ApprovalTransitionBlocked, "記錄 7 的狀態轉移需要送出簽核，這次請求改動了它，已取消", [7L] },
        { MessageText.ApprovalNotApprovalTransition, "這個轉移不需要送出簽核", [] },
        { MessageText.ApprovalAlreadyPending, "這張單已在簽核中", [] },
        { MessageText.ApprovalNoMatchingFlow, "沒有符合條件的簽核流程，請聯絡管理員", [] },
        { MessageText.ApprovalPickInvalid, "自選的簽核人必須是候選群組內、啟用中的其他使用者", [] },
        { MessageText.ApprovalPickCandidatesUnmatched, "這一關的候選群組已失效，不能自選，送出後由備援簽核人處理", [] },
        { MessageText.ApprovalPickCount, "請選 1 到 3 位簽核人", [1, 3] },
        { MessageText.ApprovalUserFieldNotCandidate, "簽核人不在候選群組內", [] },
        { MessageText.ApprovalUserFieldIsInitiator, "簽核人不能是發起人自己", [] },
        { MessageText.ApprovalCommentRequired, "退回必須填寫意見", [] },
        { MessageText.ApprovalCommentTooLong, "意見不能超過 1000 字", [1000] },
        { MessageText.ApprovalTaskNotFound, "找不到待簽核", [] },
        { MessageText.ApprovalStageHandled, "這一關已由他人處理", [] },
        { MessageText.ApprovalStateChanged, "簽核狀態已更新，請重新整理後再試", [] },
        { MessageText.ApprovalCannotWithdraw, "目前關卡已有人處理，不能撤回", [] },
        { MessageText.ApprovalNoPreviousStage, "前面沒有已同意的關卡，只能退回發起人", [] },
        { MessageText.ApprovalNotEditableField, "這一關不能修改這個欄位", [] },
        { MessageText.ApprovalFieldHiddenForApprover, "這個欄位對你隱藏，不能在簽核時修改", [] },
        { MessageText.ApprovalFieldReadOnlyForApprover, "這個欄位對你是唯讀，不能在簽核時修改", [] },
        { MessageText.ApprovalRejectTargetInvalid, "退回對象只能是 initiator 或 previous", [] },
        { MessageText.ApprovalRuntimeInvalid, "簽核設定與狀態機不一致，已停止並通知系統管理員", [] },
        { MessageText.ApprovalRecordGone, "找不到簽核的記錄（可能已被刪除）", [] },
        { MessageText.ApprovalNoWorkflow, "記錄所在模組沒有狀態機", [] },
        { MessageText.ApprovalFlowVersionMissing, "找不到實例綁定的流程版本", [] },
        { MessageText.ApprovalStageDefMissing, "找不到要退回的關卡定義", [] },
        { MessageText.ApprovalBoxInvalid, "分頁只能是 todo、done、submitted、all", [] },
        { MessageText.ApprovalStatusInvalid, "簽核狀態只能是 pending、approved、rejected、withdrawn、cancelled、rerouted", [] },
    };

    [Theory]
    [MemberData(nameof(ZhLiterals))]
    public void Zh_is_verbatim_and_every_locale_has_text(string key, string zh, object[] args)
    {
        MessageText.Get(null, key, args).Should().Be(zh);
        foreach (var locale in new[] { "en-US", "th", "vi" })
            MessageText.Get(locale, key, args).Should().NotBeNullOrWhiteSpace().And.NotBe(zh, locale);
    }

    private static I18nText T(string zh, string en) => new([KeyValuePair.Create("zh-TW", zh), KeyValuePair.Create("en-US", en)]);

    private static ResolveContext Ctx(string initiator)
    {
        var org = new OrgSnapshot(new Dictionary<long, OrgDepartment>(), new Dictionary<string, long>(), new[] { "admin", "buyer1" }.ToHashSet());
        var groups = new Dictionary<long, IReadOnlyList<string>> { [2] = [], [4] = Enumerable.Range(1, 21).Select(i => $"u{i}").ToList() };
        var users = Enumerable.Range(1, 21).Select(i => $"u{i}").Append("admin").Append("buyer1").ToHashSet();
        return new ResolveContext(new Module(), initiator, new Dictionary<string, JsonElement?>(), null, org with { ActiveUsers = users }, groups, ["admin"], 20, false,
            new Dictionary<long, I18nText> { [2] = T("董事會", "Board"), [4] = T("大", "Big") });
    }

    [Fact]
    public void NoApprover_names_follow_the_locale()
    {
        var r = ApproverResolver.Resolve(Ctx("admin"), T("簽核關", "Review"), ApprovalModes.Any, new(ApprovalSources.Group, GroupIds: [2]), null, ApprovalOnEmpty.Fallback);
        r.Error!.Resolve("zh-TW").Should().Be("關卡「簽核關」的來源「董事會」找不到簽核人，備援也沒有人");
        r.Error!.Resolve("en-US").Should().Be("No approver found for stage \"Review\" from source \"Board\", and there is no fallback either");
    }

    [Fact]
    public void TooManyApprovers_stage_name_follows_the_locale()
    {
        var r = ApproverResolver.Resolve(Ctx("buyer1"), T("簽核關", "Review"), ApprovalModes.Any, new(ApprovalSources.Group, GroupIds: [4]), null, ApprovalOnEmpty.Fallback);
        r.Error!.Resolve("en-US").Should().Be("Stage \"Review\" has more than 20 approvers");
    }

    [Fact]
    public void Manager_source_label_follows_the_locale()
    {
        var org = new OrgSnapshot(new Dictionary<long, OrgDepartment>(), new Dictionary<string, long>(), new[] { "admin", "buyer1" }.ToHashSet());
        var ctx = new ResolveContext(new Module(), "admin", new Dictionary<string, JsonElement?>(), null, org, new Dictionary<long, IReadOnlyList<string>>(), ["admin"], 20, false);
        var r = ApproverResolver.Resolve(ctx, T("簽核關", "Review"), ApprovalModes.Any, new(ApprovalSources.Manager, Level: 2), null, ApprovalOnEmpty.Fallback);
        r.Error!.Resolve("en-US").Should().Be("No approver found for stage \"Review\" from source \"Level 2 manager\", and there is no fallback either");
        r.Error!.Resolve("zh-TW").Should().Contain("「第 2 層主管」");
    }
}
