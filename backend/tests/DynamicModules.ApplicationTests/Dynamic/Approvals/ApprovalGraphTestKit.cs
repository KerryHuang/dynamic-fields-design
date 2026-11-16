using System.Text.Json;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>
/// graph 測試的共用輔助（計畫 fg2 Global Constraints「測試輔助集中一次」）：組流程圖定義的建構函式。
/// </summary>
public static class ApprovalGraphTestKit
{
    private static I18nText Name(string id) => new([KeyValuePair.Create("zh-TW", id)]);

    public static ApprovalNodeDef Start(string id = "start") => new(id, ApprovalNodeKinds.Start);

    public static ApprovalNodeDef End(string id = "end") => new(id, ApprovalNodeKinds.End);

    /// <summary>簽核節點：mode 是會簽（all）或簽（any）；when 是略過條件。</summary>
    public static ApprovalNodeDef Approval(string id, ApproverSource source, JsonElement? when = null, string mode = ApprovalModes.Any,
        List<long>? editable = null) =>
        new(id, ApprovalNodeKinds.Approval, Name(id), When: when, Approvers: source, Mode: mode, EditableFields: editable ?? [], OnEmpty: ApprovalOnEmpty.Fallback);

    public static ApprovalNodeDef Condition(string id) => new(id, ApprovalNodeKinds.Condition, Name(id));

    public static ApprovalNodeDef Fork(string id) => new(id, ApprovalNodeKinds.Fork, Name(id));

    public static ApprovalNodeDef Join(string id, string forkId, string mode = ApprovalModes.All) =>
        new(id, ApprovalNodeKinds.Join, ForkId: forkId, Mode: mode);

    /// <summary>通知節點（fg5 之前引擎不支援，用來測能力守門）：收件人是發起人，範本只有純文字。</summary>
    public static ApprovalNodeDef Notify(string id) =>
        new(id, ApprovalNodeKinds.Notify, Name(id), Recipients: [new NotifyRecipientDef(ApprovalNotifyRecipientKinds.Initiator)],
            Template: new NotifyTemplateDef(Name(id), Name(id)));

    /// <summary>連線；when、order、isDefault 只用在條件節點的出線。</summary>
    public static ApprovalEdgeDef E(string id, string from, string to, JsonElement? when = null, int? order = null, bool isDefault = false) =>
        new(id, from, to, When: when, Order: order, Default: isDefault ? true : null);

    public static ApprovalDefinition G(List<ApprovalNodeDef> nodes, List<ApprovalEdgeDef> edges, JsonElement? when = null) =>
        new(ApprovalShapes.Graph, when, null, null, [], nodes, edges);
}
