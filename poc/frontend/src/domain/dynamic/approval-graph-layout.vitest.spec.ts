import { describe, expect, it } from 'vitest';
import cases from '../../../../shared/approval-graph-cases.json';
import {
  ADDSIGN_H, ADDSIGN_W, NODE_H, NODE_W,
  layoutLeftToRight, type LayoutEdge, type LayoutNode, type LayoutResult,
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
    expect(cy(r, nodes, 'f')).toBe(r.height / 2);
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
