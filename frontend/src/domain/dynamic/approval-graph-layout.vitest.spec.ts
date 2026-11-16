import { describe, expect, it } from 'vitest';
import cases from '../../../../shared/approval-graph-cases.json';
import {
  ADDSIGN_H, ADDSIGN_W, NODE_H, NODE_W,
  edgeBendX, layoutLeftToRight, type LayoutEdge, type LayoutNode, type LayoutResult,
} from './approval-graph-layout';

interface CaseNode { id: string; kind: string }
interface CaseEdge { id: string; from: string; to: string }
interface SharedCase { name: string; errors: unknown[]; definition: { nodes: CaseNode[]; edges: CaseEdge[] } }

const valid = (cases.cases as unknown as SharedCase[]).filter((c) => c.errors.length === 0);

const n = (id: string, kind = 'approval'): LayoutNode => ({ id, kind });
const e = (from: string, to: string): LayoutEdge => ({ id: `${from}>${to}`, from, to });
const size = (node: LayoutNode) => (node.kind === 'addSign' ? { w: ADDSIGN_W, h: ADDSIGN_H } : { w: NODE_W, h: NODE_H });
const cy = (r: LayoutResult, nodes: LayoutNode[], id: string) => r.positions[id].y + size(nodes.find((x) => x.id === id)!).h / 2;

function overlaps(r: LayoutResult, nodes: LayoutNode[]): string[] {
  const bad: string[] = [];
  for (let i = 0; i < nodes.length; i++) {
    for (let j = i + 1; j < nodes.length; j++) {
      const a = r.positions[nodes[i].id], b = r.positions[nodes[j].id];
      const sa = size(nodes[i]), sb = size(nodes[j]);
      if (a.x < b.x + sb.w && b.x < a.x + sa.w && a.y < b.y + sb.h && b.y < a.y + sa.h) bad.push(`${nodes[i].id}/${nodes[j].id}`);
    }
  }
  return bad;
}

/** 線的折線近似（和畫布的自訂線一致）：水平到轉折點 x、垂直、再水平；回傳穿過「別的」節點框內部的節點。 */
function crossings(r: LayoutResult, nodes: LayoutNode[], edges: LayoutEdge[]): string[] {
  const outDeg = new Map<string, number>();
  for (const ed of edges) outDeg.set(ed.from, (outDeg.get(ed.from) ?? 0) + 1);
  const byId = new Map(nodes.map((x) => [x.id, x]));
  const bad: string[] = [];
  for (const ed of edges) {
    const a = r.positions[ed.from], b = r.positions[ed.to];
    const sa = size(byId.get(ed.from)!), sb = size(byId.get(ed.to)!);
    const y1 = a.y + sa.h / 2, y2 = b.y + sb.h / 2, x1 = a.x + sa.w, x2 = b.x;
    const bx = edgeBendX(a, b, (outDeg.get(ed.from) ?? 0) >= 2);
    const segs = [[x1, y1, bx, y1], [bx, y1, bx, y2], [bx, y2, x2, y2]];
    for (const x of nodes) {
      if (x.id === ed.from || x.id === ed.to) continue;
      const p = r.positions[x.id], sz = size(x);
      const hit = segs.some(([ax, ay, bx2, by2]) => Math.max(ax, bx2) > p.x && Math.min(ax, bx2) < p.x + sz.w && Math.max(ay, by2) > p.y && Math.min(ay, by2) < p.y + sz.h);
      if (hit) bad.push(`${ed.id}:${x.id}`);
    }
  }
  return bad;
}
const edgeIds = (list: LayoutEdge[]) => list.map((x, i) => ({ ...x, id: x.id || `e${i}` }));

describe('approval graph layout', () => {
  it('has 35 valid shared cases', () => {
    expect(valid).toHaveLength(35);
  });

  it.each(valid.map((c) => [c.name, c] as const))('every valid shared case: no two node boxes overlap (%s)', (_n, c) => {
    const r = layoutLeftToRight(c.definition.nodes, c.definition.edges);
    expect(Object.keys(r.positions).sort()).toEqual(c.definition.nodes.map((x) => x.id).sort());
    expect(overlaps(r, c.definition.nodes)).toEqual([]);
  });

  it.each(valid.map((c) => [c.name, c] as const))('every valid shared case: every edge goes left to right (%s)', (_n, c) => {
    const r = layoutLeftToRight(c.definition.nodes, c.definition.edges);
    for (const ed of c.definition.edges) expect(r.positions[ed.to].x).toBeGreaterThan(r.positions[ed.from].x);
  });

  it.each(valid.map((c) => [c.name, c] as const))('every valid shared case: no edge path crosses another node box (%s)', (_n, c) => {
    const r = layoutLeftToRight(c.definition.nodes, c.definition.edges);
    expect(crossings(r, c.definition.nodes, edgeIds(c.definition.edges))).toEqual([]);
  });

  it('the default line of a condition runs through the empty lane, not through a sibling branch (AG-001 shape)', () => {
    const nodes = [n('s', 'start'), n('c', 'condition'), n('f', 'fork'), n('law'), n('fin'), n('j', 'join'), n('hr'), n('t', 'end')];
    const edges = [e('s', 'c'), e('c', 'f'), e('c', 'hr'), e('f', 'law'), e('f', 'fin'), e('law', 'j'), e('fin', 'j'), e('j', 'hr'), e('hr', 't')];
    const r = layoutLeftToRight(nodes, edges);
    expect(crossings(r, nodes, edges)).toEqual([]);
    expect(cy(r, nodes, 'c')).toBe(cy(r, nodes, 'hr'));
    expect(overlaps(r, nodes)).toEqual([]);
  });

  it('a shorter lane joining a later column bends in the last column gap, not over the neighbour lane', () => {
    const nodes = [n('s', 'start'), n('f', 'fork'), n('a'), n('b1'), n('b2'), n('c'), n('j', 'join'), n('t', 'end')];
    const edges = [e('s', 'f'), e('f', 'a'), e('f', 'b1'), e('f', 'c'), e('b1', 'b2'), e('a', 'j'), e('b2', 'j'), e('c', 'j'), e('j', 't')];
    const r = layoutLeftToRight(nodes, edges);
    expect(crossings(r, nodes, edges)).toEqual([]);
    // 默認的「中點轉折」會切過 b2；轉折點改在 join 左邊的欄距
    expect(edgeBendX(r.positions.a, r.positions.j, false)).toBe(r.positions.j.x - 28);
  });

  it('edgeBendX: branching edges bend in the first gap after the source, others in the last gap before the target', () => {
    const col = (k: number) => k * (NODE_W + 56);
    expect(edgeBendX({ x: col(1) }, { x: col(4) }, true)).toBe(col(1) + NODE_W + 28);
    expect(edgeBendX({ x: col(1) }, { x: col(4) }, false)).toBe(col(4) - 28);
    // 加簽小節點在欄內置中，轉折點仍在欄距
    expect(edgeBendX({ x: col(2) + 28 }, { x: col(3) + 28 }, false)).toBe(col(3) - 28);
    expect(edgeBendX({ x: col(2) + 28 }, { x: col(3) }, true)).toBe(col(2) + NODE_W + 28);
  });

  it('a straight line keeps one row', () => {
    const nodes = [n('s', 'start'), n('a'), n('b'), n('t', 'end')];
    const r = layoutLeftToRight(nodes, [e('s', 'a'), e('a', 'b'), e('b', 't')]);
    expect(new Set(nodes.map((x) => r.positions[x.id].y))).toEqual(new Set([0]));
    expect(r.height).toBe(NODE_H);
  });

  it('fork branches stack top to bottom in edge order and the join sits at their vertical centre', () => {
    const nodes = [n('s', 'start'), n('f', 'fork'), n('a'), n('b'), n('c'), n('j', 'join'), n('t', 'end')];
    const r = layoutLeftToRight(nodes, [e('s', 'f'), e('f', 'b'), e('f', 'a'), e('f', 'c'), e('b', 'j'), e('a', 'j'), e('c', 'j'), e('j', 't')]);
    expect(r.positions.b.y).toBeLessThan(r.positions.a.y);
    expect(r.positions.a.y).toBeLessThan(r.positions.c.y);
    expect(cy(r, nodes, 'f')).toBe(cy(r, nodes, 'a'));
    expect(cy(r, nodes, 'j')).toBe(cy(r, nodes, 'a'));
    expect(overlaps(r, nodes)).toEqual([]);
  });

  it('nested forks stack inside their branch', () => {
    const nodes = [n('s', 'start'), n('f', 'fork'), n('x'), n('g', 'fork'), n('p'), n('q'), n('k', 'join'), n('y'), n('j', 'join'), n('t', 'end')];
    const r = layoutLeftToRight(nodes, [
      e('s', 'f'), e('f', 'x'), e('f', 'g'), e('g', 'p'), e('g', 'q'), e('p', 'k'), e('q', 'k'), e('x', 'j'), e('k', 'y'), e('y', 'j'), e('j', 't'),
    ]);
    // 內層分支夾在外層兩條泳道的第二條裡：x 在上，p、q 在下，且 p、q 都比 x 低
    expect(r.positions.x.y).toBeLessThan(r.positions.p.y);
    expect(r.positions.p.y).toBeLessThan(r.positions.q.y);
    expect(cy(r, nodes, 'g')).toBe((cy(r, nodes, 'p') + cy(r, nodes, 'q')) / 2);
    expect(cy(r, nodes, 'k')).toBe(cy(r, nodes, 'g'));
    expect(cy(r, nodes, 'j')).toBe(cy(r, nodes, 'f'));
    expect(overlaps(r, nodes)).toEqual([]);
  });

  it('condition branches stack until their common successor', () => {
    const nodes = [n('s', 'start'), n('c', 'condition'), n('a'), n('t', 'end')];
    const r = layoutLeftToRight(nodes, [e('s', 'c'), e('c', 'a'), e('c', 't'), e('a', 't')]);
    // 第一條線經簽核（上），預設線直接到結束（結束在後必經位置，與條件節點同一水平中心）
    expect(cy(r, nodes, 'c')).toBe(cy(r, nodes, 't'));
    expect(r.positions.a.x).toBeGreaterThan(r.positions.c.x);
    expect(r.positions.t.x).toBeGreaterThan(r.positions.a.x);
    expect(overlaps(r, nodes)).toEqual([]);
  });

  it('a condition inside a fork branch whose both edges go straight to the join', () => {
    const nodes = [n('s', 'start'), n('f', 'fork'), n('c', 'condition'), n('b'), n('j', 'join'), n('t', 'end')];
    const r = layoutLeftToRight(nodes, [e('s', 'f'), e('f', 'c'), e('f', 'b'), e('c', 'j'), e('c', 'j'), e('b', 'j'), e('j', 't')]);
    expect(Object.keys(r.positions)).toHaveLength(6);
    expect(overlaps(r, nodes)).toEqual([]);
    expect(r.positions.j.x).toBeGreaterThan(r.positions.c.x);
    expect(r.positions.c.y).toBeLessThan(r.positions.b.y);
    expect(cy(r, nodes, 'j')).toBe(cy(r, nodes, 'f'));
  });

  it('several end nodes: each condition edge ends on its own end', () => {
    const nodes = [n('s', 'start'), n('c', 'condition'), n('a'), n('e1', 'end'), n('e2', 'end')];
    const r = layoutLeftToRight(nodes, [e('s', 'c'), e('c', 'a'), e('c', 'e2'), e('a', 'e1')]);
    expect(Object.keys(r.positions)).toHaveLength(5);
    expect(overlaps(r, nodes)).toEqual([]);
    expect(r.positions.a.y).toBeLessThan(r.positions.e2.y);
    expect(r.positions.e1.y).toBe(r.positions.a.y);
    for (const ed of [e('c', 'a'), e('c', 'e2'), e('a', 'e1')]) expect(r.positions[ed.to].x).toBeGreaterThan(r.positions[ed.from].x);
  });

  it('an empty branch (fork straight to join) still gets its own lane', () => {
    const nodes = [n('s', 'start'), n('f', 'fork'), n('a'), n('j', 'join'), n('t', 'end')];
    const r = layoutLeftToRight(nodes, [e('s', 'f'), e('f', 'a'), e('f', 'j'), e('a', 'j'), e('j', 't')]);
    expect(r.height).toBe(NODE_H * 2 + 24);
    // 主幹走在空泳道（第二條）的中心，直線不會穿過另一條泳道的節點
    expect(cy(r, nodes, 'f')).toBe(NODE_H + 24 + NODE_H / 2);
    expect(cy(r, nodes, 'j')).toBe(cy(r, nodes, 'f'));
    expect(crossings(r, nodes, [e('s', 'f'), e('f', 'a'), e('f', 'j'), e('a', 'j'), e('j', 't')])).toEqual([]);
    expect(overlaps(r, nodes)).toEqual([]);
  });

  it('add-sign nodes use the small size in their own column without overlapping', () => {
    const nodes = [n('s', 'start'), n('a'), n('as-1', 'addSign'), n('b'), n('t', 'end')];
    const r = layoutLeftToRight(nodes, [e('s', 'a'), e('a', 'as-1'), e('as-1', 'b'), e('b', 't')]);
    const col = (id: string) => Math.round(r.positions[id].x / (NODE_W + 56));
    expect(new Set(nodes.map((x) => col(x.id))).size).toBe(5);
    expect(r.positions['as-1'].x).toBe((NODE_W - ADDSIGN_W) / 2 + col('as-1') * (NODE_W + 56));
    expect(cy(r, nodes, 'as-1')).toBe(cy(r, nodes, 'a'));
    expect(overlaps(r, nodes)).toEqual([]);
  });

  it('an invalid or cyclic graph falls back to columns without throwing', () => {
    const cyc = [n('s', 'start'), n('a'), n('b'), n('t', 'end')];
    const rc = layoutLeftToRight(cyc, [e('s', 'a'), e('a', 'b'), e('b', 'a'), e('b', 't')]);
    expect(Object.keys(rc.positions).sort()).toEqual(['a', 'b', 's', 't']);
    expect(overlaps(rc, cyc)).toEqual([]);

    const stray = [n('s', 'start'), n('a'), n('lost'), n('lost2'), n('t', 'end')];
    const rs = layoutLeftToRight(stray, [e('s', 'a'), e('a', 't'), e('lost', 'lost2')]);
    expect(Object.keys(rs.positions)).toHaveLength(5);
    expect(rs.positions.lost.x).toBeGreaterThan(rs.positions.t.x);
    expect(overlaps(rs, stray)).toEqual([]);

    const dangling = layoutLeftToRight([n('s', 'start'), n('a')], [e('s', 'a'), e('a', 'ghost')]);
    expect(Object.keys(dangling.positions).sort()).toEqual(['a', 's']);
    expect(layoutLeftToRight([], [])).toEqual({ positions: {}, width: 0, height: 0 });
  });

  it('the same input gives the same output', () => {
    for (const c of valid) {
      expect(layoutLeftToRight(c.definition.nodes, c.definition.edges)).toEqual(layoutLeftToRight(c.definition.nodes, c.definition.edges));
    }
  });
});
