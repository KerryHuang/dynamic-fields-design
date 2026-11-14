namespace DynamicModules.Domain.Approvals;

/// <summary>由定義建出的圖：節點索引、出入線、可達與無環判斷。只收「代號合法、端點存在」的節點與連線，格式問題由驗證器先報。</summary>
public sealed class ApprovalGraphModel
{
    /// <summary>Index 是在定義陣列中的位置，用來定出「先後」與錯誤鍵。</summary>
    public sealed record Edge(int Index, ApprovalEdgeDef Def);

    private readonly Dictionary<string, List<Edge>> _out = new(), _in = new();

    public IReadOnlyList<ApprovalNodeDef> Nodes { get; }
    public IReadOnlyList<Edge> Edges { get; }
    public IReadOnlyDictionary<string, ApprovalNodeDef> ById { get; }
    public IReadOnlyDictionary<string, int> NodeOrder { get; }

    public ApprovalGraphModel(IReadOnlyList<(int Index, ApprovalNodeDef Node)> nodes, IReadOnlyList<(int Index, ApprovalEdgeDef Def)> edges)
    {
        Nodes = [.. nodes.Select(n => n.Node)];
        ById = Nodes.ToDictionary(n => n.Id);
        NodeOrder = nodes.ToDictionary(n => n.Node.Id, n => n.Index);
        Edges = [.. edges.Select(e => new Edge(e.Index, e.Def))];
        foreach (var n in Nodes) { _out[n.Id] = []; _in[n.Id] = []; }
        foreach (var e in Edges) { _out[e.Def.From].Add(e); _in[e.Def.To].Add(e); }
    }

    public IReadOnlyList<Edge> Out(string id) => _out[id];
    public IReadOnlyList<Edge> In(string id) => _in[id];

    public IEnumerable<ApprovalNodeDef> OfKind(string kind) => Nodes.Where(n => n.Kind == kind);

    /// <summary>從 starts 出發（含 starts 本身）沿線走到的節點；不進入 avoid；reverse 時沿線反向走。</summary>
    public HashSet<string> Reach(IEnumerable<string> starts, string? avoid = null, bool reverse = false)
    {
        var seen = new HashSet<string>();
        var stack = new Stack<string>(starts.Where(s => s != avoid));
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!seen.Add(id)) continue;
            foreach (var e in reverse ? _in[id] : _out[id])
            {
                var next = reverse ? e.Def.From : e.Def.To;
                if (next != avoid) stack.Push(next);
            }
        }
        return seen;
    }

    public bool IsAcyclic()
    {
        var indeg = Nodes.ToDictionary(n => n.Id, n => _in[n.Id].Count);
        var queue = new Queue<string>(indeg.Where(p => p.Value == 0).Select(p => p.Key));
        var done = 0;
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            done++;
            foreach (var e in _out[id])
                if (--indeg[e.Def.To] == 0) queue.Enqueue(e.Def.To);
        }
        return done == Nodes.Count;
    }
}
