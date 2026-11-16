// 簽核進度圖由左到右的自動排版（純函式，無 UI 依賴；設計器之後共用）。
// 欄（x）：從開始節點的最長路徑層數。列（y）：分支節點與多出線條件節點以「區塊」遞迴放置，
// 區塊終點是直接後必經節點，每條出線一條泳道由上往下疊。分解不了就退回「每欄由上而下」。

export const NODE_W = 168;
export const NODE_H = 84;
export const ADDSIGN_W = 112;
export const ADDSIGN_H = 48;
export const COL_GAP = 56;
export const ROW_GAP = 24;

export interface LayoutNode { id: string; kind: string }
export interface LayoutEdge { id: string; from: string; to: string }
export interface LayoutResult {
  positions: Record<string, { x: number; y: number }>;
  width: number;
  height: number;
}

const EXIT = '\0exit';
const heightOf = (n: LayoutNode) => (n.kind === 'addSign' ? ADDSIGN_H : NODE_H);
const widthOf = (n: LayoutNode) => (n.kind === 'addSign' ? ADDSIGN_W : NODE_W);

class Undecomposable extends Error {}

interface Graph {
  byId: Map<string, LayoutNode>;
  out: Map<string, string[]>; // 依線的定義順序
  inDeg: Map<string, number>;
}

function build(nodes: LayoutNode[], edges: LayoutEdge[]): Graph | null {
  const byId = new Map<string, LayoutNode>();
  for (const n of nodes) {
    if (byId.has(n.id)) return null;
    byId.set(n.id, n);
  }
  const out = new Map<string, string[]>(nodes.map((n) => [n.id, []]));
  const inDeg = new Map<string, number>(nodes.map((n) => [n.id, 0]));
  for (const e of edges) {
    if (!byId.has(e.from) || !byId.has(e.to)) return null;
    out.get(e.from)!.push(e.to);
    inDeg.set(e.to, inDeg.get(e.to)! + 1);
  }
  return { byId, out, inDeg };
}

function findStart(nodes: LayoutNode[], g: Graph): string {
  return (nodes.find((n) => n.kind === 'start') ?? nodes.find((n) => g.inDeg.get(n.id) === 0) ?? nodes[0]).id;
}

/** 從 start 可達的節點依發現順序（BFS）。 */
function discover(g: Graph, start: string): string[] {
  const seen = new Set([start]);
  const order = [start];
  for (let i = 0; i < order.length; i++) {
    for (const t of g.out.get(order[i])!) if (!seen.has(t)) { seen.add(t); order.push(t); }
  }
  return order;
}

/** 可達節點的欄：無環時是最長路徑層數，有環時退回 BFS 深度。 */
function columns(g: Graph, start: string, reach: string[]): { col: Map<string, number>; acyclic: boolean } {
  const inSet = new Set(reach);
  const indeg = new Map<string, number>(reach.map((id) => [id, 0]));
  for (const id of reach) for (const t of g.out.get(id)!) if (inSet.has(t)) indeg.set(t, indeg.get(t)! + 1);
  const col = new Map<string, number>(reach.map((id) => [id, 0]));
  const queue = reach.filter((id) => indeg.get(id) === 0);
  let done = 0;
  for (let i = 0; i < queue.length; i++) {
    const id = queue[i];
    done++;
    for (const t of g.out.get(id)!) {
      col.set(t, Math.max(col.get(t)!, col.get(id)! + 1));
      indeg.set(t, indeg.get(t)! - 1);
      if (indeg.get(t) === 0) queue.push(t);
    }
  }
  if (done === reach.length && indeg.get(start) === 0) return { col, acyclic: true };
  const depth = new Map<string, number>([[start, 0]]);
  for (const id of reach) for (const t of g.out.get(id)!) if (!depth.has(t) && depth.has(id)) depth.set(t, depth.get(id)! + 1);
  const bfs = new Map<string, number>();
  const q = [start];
  bfs.set(start, 0);
  for (let i = 0; i < q.length; i++) {
    for (const t of g.out.get(q[i])!) if (!bfs.has(t)) { bfs.set(t, bfs.get(q[i])! + 1); q.push(t); }
  }
  return { col: bfs, acyclic: false };
}

/** 直接後必經節點（以虛擬終點接所有沒有出線的節點）；null 表示只有虛擬終點。 */
function immediatePostDominators(g: Graph, reach: string[]): Map<string, string | null> {
  const all = new Set<string>([...reach, EXIT]);
  const pdom = new Map<string, Set<string>>();
  for (const id of reach) pdom.set(id, new Set(all));
  pdom.set(EXIT, new Set([EXIT]));
  for (let changed = true; changed;) {
    changed = false;
    for (let i = reach.length - 1; i >= 0; i--) {
      const id = reach[i];
      const succ = g.out.get(id)!;
      const targets = succ.length ? succ : [EXIT];
      const inter = new Set(pdom.get(targets[0])!);
      for (const s of targets.slice(1)) for (const x of [...inter]) if (!pdom.get(s)!.has(x)) inter.delete(x);
      inter.add(id);
      if (inter.size !== pdom.get(id)!.size) { pdom.set(id, inter); changed = true; }
    }
  }
  const ipdom = new Map<string, string | null>();
  for (const id of reach) {
    const strict = [...pdom.get(id)!].filter((x) => x !== id);
    const d = strict.find((x) => pdom.get(x)!.size === strict.length);
    ipdom.set(id, d === undefined || d === EXIT ? null : d);
  }
  return ipdom;
}

interface Segment { height: number; items: Array<[string, number]> } // 中心 y，相對區段頂端

function layoutRows(g: Graph, start: string, reach: string[]): { centers: Map<string, number>; height: number } {
  const ipdom = immediatePostDominators(g, reach);
  const placed = new Set<string>();

  // 從 from 走到 stop（不含）為止的一串；stop 為 null 時走到沒有出線的節點。
  const chain = (from: string, stop: string | null): Segment => {
    const segs: Segment[] = [];
    let cur: string | null = from;
    while (cur !== null && cur !== stop) {
      if (placed.has(cur)) throw new Undecomposable();
      placed.add(cur);
      const node = g.byId.get(cur)!;
      const outs: string[] = g.out.get(cur)!;
      if (outs.length >= 2) {
        const join: string | null = ipdom.get(cur) ?? null;
        const lanes: Segment[] = outs.map((t) => (t === join ? { height: NODE_H, items: [] } : chain(t, join)));
        const total = lanes.reduce((s, l) => s + l.height, 0) + ROW_GAP * (lanes.length - 1);
        const items: Array<[string, number]> = [[cur, total / 2]];
        let top = 0;
        for (const l of lanes) {
          for (const [id, c] of l.items) items.push([id, top + c]);
          top += l.height + ROW_GAP;
        }
        segs.push({ height: total, items });
        cur = join;
      } else {
        segs.push({ height: heightOf(node), items: [[cur, heightOf(node) / 2]] });
        cur = outs.length ? outs[0] : null;
      }
    }
    if (stop !== null && cur !== stop) throw new Undecomposable();
    const height = Math.max(0, ...segs.map((s) => s.height));
    const items: Array<[string, number]> = [];
    for (const s of segs) for (const [id, c] of s.items) items.push([id, height / 2 - s.height / 2 + c]);
    return { height, items };
  };

  const main = chain(start, null);
  if (placed.size !== reach.length) throw new Undecomposable();
  return { centers: new Map(main.items), height: main.height };
}

function layoutFallback(g: Graph, nodes: LayoutNode[], start: string, reach: string[]): LayoutResult {
  const { col } = columns(g, start, reach);
  let maxCol = Math.max(0, ...col.values());
  const byCol = new Map<number, string[]>();
  const put = (id: string, c: number) => byCol.set(c, [...(byCol.get(c) ?? []), id]);
  for (const id of reach) put(id, col.get(id) ?? 0);
  const placedIds = new Set(reach);
  const stray = nodes.filter((n) => !placedIds.has(n.id));
  if (stray.length) { maxCol += 1; for (const n of stray) put(n.id, maxCol); }
  const heights = new Map<number, number>();
  for (const [c, ids] of byCol) {
    heights.set(c, ids.reduce((s, id) => s + heightOf(g.byId.get(id)!), 0) + ROW_GAP * (ids.length - 1));
  }
  const height = Math.max(0, ...heights.values());
  const positions: LayoutResult['positions'] = {};
  for (const [c, ids] of byCol) {
    let y = (height - heights.get(c)!) / 2;
    for (const id of ids) {
      const n = g.byId.get(id)!;
      positions[id] = { x: c * (NODE_W + COL_GAP) + (NODE_W - widthOf(n)) / 2, y };
      y += heightOf(n) + ROW_GAP;
    }
  }
  return { positions, width: (maxCol + 1) * NODE_W + maxCol * COL_GAP, height };
}

export function layoutLeftToRight(nodes: LayoutNode[], edges: LayoutEdge[]): LayoutResult {
  if (nodes.length === 0) return { positions: {}, width: 0, height: 0 };
  const g = build(nodes, edges);
  if (!g) {
    // 重複節點或線指向不存在的節點：退回，重複代號只留第一個
    const seen = new Set<string>();
    const uniq = nodes.filter((n) => !seen.has(n.id) && !!seen.add(n.id));
    const ok = edges.filter((e) => seen.has(e.from) && seen.has(e.to));
    return layoutLeftToRight(uniq, ok);
  }
  const start = findStart(nodes, g);
  const reach = discover(g, start);
  const { col, acyclic } = columns(g, start, reach);
  if (acyclic && reach.length === nodes.length) {
    try {
      const { centers, height } = layoutRows(g, start, reach);
      const positions: LayoutResult['positions'] = {};
      let maxCol = 0;
      for (const [id, c] of centers) {
        const n = g.byId.get(id)!;
        const k = col.get(id)!;
        maxCol = Math.max(maxCol, k);
        positions[id] = { x: k * (NODE_W + COL_GAP) + (NODE_W - widthOf(n)) / 2, y: c - heightOf(n) / 2 };
      }
      return { positions, width: (maxCol + 1) * NODE_W + maxCol * COL_GAP, height };
    } catch (e) {
      if (!(e instanceof Undecomposable)) throw e;
    }
  }
  return layoutFallback(g, nodes, start, reach);
}
