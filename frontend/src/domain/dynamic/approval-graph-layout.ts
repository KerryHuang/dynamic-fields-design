// 簽核進度圖由左到右的自動排版（純函式，無 UI 依賴；設計器之後共用）。
// 欄（x）：從開始節點的最長路徑層數。列（y）：分支節點與多出線條件節點以「區塊」遞迴放置，
// 區塊終點是直接後必經節點，每條出線一條泳道由上往下疊。分解不了就退回「每欄由上而下」。

export const NODE_W = 168;
export const NODE_H = 84;
export const ADDSIGN_W = 112;
export const ADDSIGN_H = 48;
export const COL_GAP = 56;
/** 有條件標籤的線，起點右側第一個欄距（約 8 個中文字）；只在傳了 labeledEdgeIds 時使用。 */
export const LABELED_COL_GAP = 136;
export const ROW_GAP = 24;

export interface LayoutNode { id: string; kind: string }
export interface LayoutEdge { id: string; from: string; to: string }
export interface LayoutResult {
  positions: Record<string, { x: number; y: number }>;
  width: number;
  height: number;
  /** 節點代號 → 該節點所在欄右側的欄距；只在傳了 labeledEdgeIds 時有（給 edgeBendX 的 gapAfterFrom）。 */
  gapAfter?: Record<string, number>;
}
export interface LayoutOptions {
  /** 有標籤的線；它們起點右側第一個欄距加寬到 LABELED_COL_GAP。不傳時輸出與不加寬時逐位元相同。 */
  labeledEdgeIds?: Set<string>;
}

const EXIT = '\0exit';
const heightOf = (n: LayoutNode) => (n.kind === 'addSign' ? ADDSIGN_H : NODE_H);
const widthOf = (n: LayoutNode) => (n.kind === 'addSign' ? ADDSIGN_W : NODE_W);

/** 每欄右側的欄距與欄的左緣 x。不傳 labeledEdgeIds 時等於 k * (NODE_W + COL_GAP)。 */
function columnGeometry(g: Graph, edges: LayoutEdge[], col: Map<string, number>, opts: LayoutOptions | undefined) {
  const wide = new Set<number>();
  if (opts?.labeledEdgeIds) {
    for (const ed of edges) {
      const c = col.get(ed.from);
      if (opts.labeledEdgeIds.has(ed.id) && c !== undefined && g.byId.has(ed.from)) wide.add(c);
    }
  }
  const gap = (c: number) => (wide.has(c) ? LABELED_COL_GAP : COL_GAP);
  const xOf = (k: number) => { let x = 0; for (let j = 0; j < k; j++) x += NODE_W + gap(j); return x; };
  const widthTo = (maxCol: number) => xOf(maxCol) + NODE_W;
  return { gap, xOf, widthTo };
}

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

// 中心 y 都相對區段頂端。anchor 是這一段「主幹」穿過的水平線：串接時各段依 anchor 對齊，前後節點因此在同一條線上。
interface Segment { height: number; anchor: number; items: Array<[string, number]> }

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
        const lanes: Segment[] = outs.map((t) => (t === join ? { height: NODE_H, anchor: NODE_H / 2, items: [] } : chain(t, join)));
        const total = lanes.reduce((s, l) => s + l.height, 0) + ROW_GAP * (lanes.length - 1);
        const tops: number[] = [];
        let top = 0;
        for (const l of lanes) { tops.push(top); top += l.height + ROW_GAP; }
        // 有空泳道（分支直接接匯合）時，主幹走在空泳道上，那條線才不會穿過別條泳道的節點；否則在區塊正中。
        const empty = outs.findIndex((t) => t === join);
        const anchor = empty >= 0 ? tops[empty] + NODE_H / 2 : total / 2;
        const items: Array<[string, number]> = [[cur, anchor]];
        lanes.forEach((l, i) => { for (const [id, c] of l.items) items.push([id, tops[i] + c]); });
        segs.push({ height: total, anchor, items });
        cur = join;
      } else {
        segs.push({ height: heightOf(node), anchor: heightOf(node) / 2, items: [[cur, heightOf(node) / 2]] });
        cur = outs.length ? outs[0] : null;
      }
    }
    if (stop !== null && cur !== stop) throw new Undecomposable();
    const above = Math.max(0, ...segs.map((s) => s.anchor));
    const below = Math.max(0, ...segs.map((s) => s.height - s.anchor));
    const items: Array<[string, number]> = [];
    for (const s of segs) for (const [id, c] of s.items) items.push([id, above - s.anchor + c]);
    return { height: above + below, anchor: above, items };
  };

  const main = chain(start, null);
  if (placed.size !== reach.length) throw new Undecomposable();
  return { centers: new Map(main.items), height: main.height };
}

function layoutFallback(g: Graph, nodes: LayoutNode[], edges: LayoutEdge[], start: string, reach: string[], opts: LayoutOptions | undefined): LayoutResult {
  const { col } = columns(g, start, reach);
  let maxCol = Math.max(0, ...col.values());
  const byCol = new Map<number, string[]>();
  const put = (id: string, c: number) => byCol.set(c, [...(byCol.get(c) ?? []), id]);
  for (const id of reach) put(id, col.get(id) ?? 0);
  const geo = columnGeometry(g, edges, col, opts);
  const placedIds = new Set(reach);
  const stray = nodes.filter((n) => !placedIds.has(n.id));
  if (stray.length) { maxCol += 1; for (const n of stray) put(n.id, maxCol); }
  const heights = new Map<number, number>();
  for (const [c, ids] of byCol) {
    heights.set(c, ids.reduce((s, id) => s + heightOf(g.byId.get(id)!), 0) + ROW_GAP * (ids.length - 1));
  }
  const height = Math.max(0, ...heights.values());
  const positions: LayoutResult['positions'] = {};
  const gapAfter: Record<string, number> = {};
  for (const [c, ids] of byCol) {
    let y = (height - heights.get(c)!) / 2;
    for (const id of ids) {
      const n = g.byId.get(id)!;
      positions[id] = { x: geo.xOf(c) + (NODE_W - widthOf(n)) / 2, y };
      gapAfter[id] = geo.gap(c);
      y += heightOf(n) + ROW_GAP;
    }
  }
  return { positions, width: geo.widthTo(maxCol), height, ...(opts?.labeledEdgeIds ? { gapAfter } : {}) };
}

export function layoutLeftToRight(nodes: LayoutNode[], edges: LayoutEdge[], opts?: LayoutOptions): LayoutResult {
  if (nodes.length === 0) return { positions: {}, width: 0, height: 0 };
  const g = build(nodes, edges);
  if (!g) {
    // 重複節點或線指向不存在的節點：退回，重複代號只留第一個
    const seen = new Set<string>();
    const uniq = nodes.filter((n) => !seen.has(n.id) && !!seen.add(n.id));
    const ok = edges.filter((e) => seen.has(e.from) && seen.has(e.to));
    return layoutLeftToRight(uniq, ok, opts);
  }
  const start = findStart(nodes, g);
  const reach = discover(g, start);
  const { col, acyclic } = columns(g, start, reach);
  if (acyclic && reach.length === nodes.length) {
    try {
      const { centers, height } = layoutRows(g, start, reach);
      const geo = columnGeometry(g, edges, col, opts);
      const positions: LayoutResult['positions'] = {};
      const gapAfter: Record<string, number> = {};
      let maxCol = 0;
      for (const [id, c] of centers) {
        const n = g.byId.get(id)!;
        const k = col.get(id)!;
        maxCol = Math.max(maxCol, k);
        positions[id] = { x: geo.xOf(k) + (NODE_W - widthOf(n)) / 2, y: c - heightOf(n) / 2 };
        gapAfter[id] = geo.gap(k);
      }
      return { positions, width: geo.widthTo(maxCol), height, ...(opts?.labeledEdgeIds ? { gapAfter } : {}) };
    } catch (e) {
      if (!(e instanceof Undecomposable)) throw e;
    }
  }
  return layoutFallback(g, nodes, edges, start, reach, opts);
}

/** 節點框：x 是左上角，width 預設 NODE_W（加簽小節點是 ADDSIGN_W，在欄內置中）。 */
export interface BoxX { x: number; width?: number }

/** 節點所在「欄位框」的左緣：由節點中心推回，小節點與一般節點在同一欄得到同一個值。 */
const slotLeft = (b: BoxX) => b.x + (b.width ?? NODE_W) / 2 - NODE_W / 2;

/**
 * 線的轉折點 x（圖座標）：從分支、條件（兩條以上出線）出發的線在起點欄位框右緣再過 gapAfterFrom 的一半處轉折，
 * 其他線在終點欄位框左緣之前半個 COL_GAP 處轉折。自動排版時節點都在欄位格上，轉折點因此落在欄距；
 * 手排時同一條規則直接吃座標（gapAfterFrom 由 manualGapAfter 或自動排版的 gapAfter 提供）。
 */
export function edgeBendX(from: BoxX, to: BoxX, branching: boolean, gapAfterFrom: number = COL_GAP): number {
  return branching ? slotLeft(from) + NODE_W + gapAfterFrom / 2 : slotLeft(to) - COL_GAP / 2;
}

/** 手排時起點右側的欄距：起點欄位框右緣到最近一個在右側的出線終點欄位框左緣，夾在 COL_GAP 到 LABELED_COL_GAP；沒有右側終點用 COL_GAP。 */
export function manualGapAfter(from: BoxX, targets: BoxX[]): number {
  const right = slotLeft(from) + NODE_W;
  const dists = targets.map((t) => slotLeft(t) - right).filter((d) => d > 0);
  if (!dists.length) return COL_GAP;
  return Math.min(LABELED_COL_GAP, Math.max(COL_GAP, Math.min(...dists)));
}
