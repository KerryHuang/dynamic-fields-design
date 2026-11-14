using System.Text.Json;

namespace DynamicModules.Domain.Approvals;

/// <summary>條件求值結果：成立、不成立、無法判斷（引用的欄位或選項已不在）。</summary>
public enum ConditionTruth { Holds, Fails, Undecidable }

/// <summary>
/// 執行期用的圖（流程圖設計 §8）：結構再驗通過、而且只含支援的節點型別才建得出來；出線依定義順序。
/// 純函式、不碰資料庫；不快取，呼叫端每次動作建一次。
/// </summary>
public sealed class ApprovalGraphRuntime
{
    private readonly ApprovalGraphModel _model;
    private readonly Dictionary<string, ApprovalEdgeDef> _edges;
    private readonly Dictionary<string, ApprovalNodeDef> _joinOfFork = new();
    private readonly Dictionary<(string Join, string InEdge), string> _arm = new();

    private ApprovalGraphRuntime(ApprovalDefinition d)
    {
        _model = new ApprovalGraphModel(
            [.. d.Nodes!.Select((n, i) => (i, n))], [.. d.Edges!.Select((e, i) => (i, e))]);
        _edges = _model.Edges.ToDictionary(e => e.Def.Id, e => e.Def);
        Start = _model.OfKind(ApprovalNodeKinds.Start).Single();
        foreach (var j in _model.OfKind(ApprovalNodeKinds.Join)) _joinOfFork[j.ForkId!] = j;

        // J13：建圖時一次算好每條匯合進線屬於配對分支節點的哪一條出線。
        // 進線起點就是分支節點本身（直連的空分支）→ 這條線；否則是「從出線終點走得到進線起點」的那一條（G7 保證各分支不共用節點）。
        foreach (var j in _model.OfKind(ApprovalNodeKinds.Join))
        {
            var fork = _model.ById[j.ForkId!];
            var arms = _model.Out(fork.Id).Select(e => (Edge: e.Def, Reach: _model.Reach([e.Def.To], j.Id))).ToList();
            foreach (var inEdge in _model.In(j.Id))
            {
                var from = inEdge.Def.From;
                _arm[(j.Id, inEdge.Def.Id)] = from == fork.Id ? inEdge.Def.Id : arms.First(a => a.Reach.Contains(from)).Edge.Id;
            }
        }
    }

    /// <summary>J7：結構驗證有任何錯誤、或有節點型別不在 supportedKinds，就回 null 與錯誤；政策上限不在這裡擋，警告不看。</summary>
    public static ApprovalGraphRuntime? Build(ApprovalDefinition d, IReadOnlySet<string> supportedKinds, out IReadOnlyList<GraphIssue> errors)
    {
        var list = new List<GraphIssue>(
            ApprovalGraphValidator.Check(d, new GraphPolicyLimits(int.MaxValue, int.MaxValue), previous: null).Errors);
        // 結構沒有錯時節點代號必然合法且不重複，不支援的型別用節點鍵報；有結構錯誤時節點可能殘缺，只報結構錯誤。
        if (list.Count == 0)
            foreach (var n in d.Nodes!.Where(n => !supportedKinds.Contains(n.Kind)))
                list.Add(new GraphIssue($"nodes.{n.Id}", "runtime", "unsupportedKind"));
        errors = list;
        return list.Count == 0 ? new ApprovalGraphRuntime(d) : null;
    }

    public ApprovalNodeDef Start { get; }

    public ApprovalNodeDef Node(string id) => _model.ById[id];

    /// <summary>節點的出線，依定義順序。</summary>
    public IReadOnlyList<ApprovalEdgeDef> Out(string nodeId) => [.. _model.Out(nodeId).Select(e => e.Def)];

    public ApprovalEdgeDef Edge(string edgeId) => _edges[edgeId];

    public ApprovalNodeDef JoinOf(string forkId) => _joinOfFork[forkId];

    public ApprovalNodeDef ForkOf(string joinId) => _model.ById[_model.ById[joinId].ForkId!];

    /// <summary>J13：匯合的這條進線屬於配對分支節點的哪一條出線（fork → join 直連就是這條線本身）。</summary>
    public string ArmOf(string joinId, string inEdgeId) => _arm[(joinId, inEdgeId)];

    /// <summary>J8：依 order 求值，第一條成立的；先遇到無法判斷就回 (null, true)；都不成立回預設線。</summary>
    public (ApprovalEdgeDef? Edge, bool Undecidable) Choose(string conditionId, Func<JsonElement, ConditionTruth> eval)
    {
        var outs = Out(conditionId);
        foreach (var e in outs.Where(e => e.Default != true).OrderBy(e => e.Order))
            switch (eval(e.When!.Value))
            {
                case ConditionTruth.Holds: return (e, false);
                case ConditionTruth.Undecidable: return (null, true);
            }
        return (outs.First(e => e.Default == true), false);
    }

    /// <summary>J3：分支節點所在段 parentKey 的出線 edgeId 開出的段；根段是空字串。</summary>
    public static string Child(string parentKey, string edgeId) => parentKey == "" ? edgeId : $"{parentKey}/{edgeId}";

    /// <summary>J3：key 在段 root 之內（不能用單純前綴，e1 與 e10）；根段（空字串）涵蓋所有段。</summary>
    public static bool Within(string key, string root) => root == "" || key == root || key.StartsWith(root + "/", StringComparison.Ordinal);
}
