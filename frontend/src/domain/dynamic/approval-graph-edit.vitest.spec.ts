import { describe, expect, it } from 'vitest';
import { reactive } from 'vue';
import cases from '../../../../shared/approval-graph-cases.json';
import type { ApprovalEdgeDef, ApprovalNodeDef, GraphDefinition } from './approval-flow-types';
import {
  addNode, canConnect, conditionOutEdges, connect, insertOnEdge, moveNode, nextCode, relayout, removeEdge, removeNode, renameCode,
  reorderConditionEdge, setEdge, setNode, topoOrder, type EditOptions, type GraphState,
} from './approval-graph-edit';
import { createHistory, push, undo } from './approval-graph-history';
import { layoutLeftToRight } from './approval-graph-layout';

const names: EditOptions['names'] = { approval: (n) => ({ 'zh-TW': `簽核 ${n}` }), conditionLabel: (n) => ({ 'zh-TW': `條件 ${n}` }) };
const opts: EditOptions = { names };

const node = (id: string, kind: ApprovalNodeDef['kind'], extra: Partial<ApprovalNodeDef> = {}): ApprovalNodeDef => ({ id, kind, ...extra });
const edge = (id: string, from: string, to: string, extra: Partial<ApprovalEdgeDef> = {}): ApprovalEdgeDef => ({ id, from, to, ...extra });
const mk = (nodes: ApprovalNodeDef[], edges: ApprovalEdgeDef[]): GraphState => {
  const definition: GraphDefinition = { shape: 'graph', stages: [], nodes, edges };
  const positions: GraphState['positions'] = {};
  nodes.forEach((x, i) => { positions[x.id] = { x: i * 224, y: 0 }; });
  return { definition, positions };
};
const appr = (id: string) => node(id, 'approval', { name: { 'zh-TW': id }, approvers: { source: 'manager', level: 1 }, mode: 'any', editableFields: [], onEmpty: 'fallback' });
const ids = (s: GraphState) => s.definition.nodes.map((x) => x.id);
const pairs = (s: GraphState) => s.definition.edges.map((x) => `${x.from}>${x.to}`).sort();
const nodeOf = (s: GraphState, id: string) => s.definition.nodes.find((x) => x.id === id)!;
const edgeOf = (s: GraphState, from: string, to: string) => s.definition.edges.find((x) => x.from === from && x.to === to)!;

/** 直線：start → mgr → fin → ceo → end */
const line = () => mk(
  [node('start', 'start'), appr('mgr'), appr('fin'), appr('ceo'), node('end', 'end')],
  [edge('e1', 'start', 'mgr'), edge('e2', 'mgr', 'fin'), edge('e3', 'fin', 'ceo'), edge('e4', 'ceo', 'end')],
);
/** 條件 c：預設線直通 b，條件 1 經 x 到 b。 */
const cond = () => mk(
  [node('start', 'start'), node('c', 'condition'), appr('x'), appr('b'), node('end', 'end')],
  [edge('e1', 'start', 'c'), edge('e2', 'c', 'b', { default: true }), edge('e3', 'c', 'x', { order: 1, when: { all: [{ field: 1, op: 'gte', value: 1 }] }, label: { 'zh-TW': '十萬以上' } }), edge('e4', 'x', 'b'), edge('e5', 'b', 'end')],
);

/** 本地不變式：線代號唯一、同一對節點一條線、每個分支恰一個匯合指向它、條件恰一條預設線、簽核節點一條出線。 */
function localInvariants(s: GraphState): string[] {
  const bad: string[] = [];
  const { nodes, edges } = s.definition;
  const eid = new Set<string>();
  const pr = new Set<string>();
  const nid = new Set<string>();
  for (const n of nodes) { if (nid.has(n.id)) bad.push(`dup node ${n.id}`); nid.add(n.id); }
  for (const e of edges) {
    if (eid.has(e.id)) bad.push(`dup edge id ${e.id}`);
    eid.add(e.id);
    const p = `${e.from}>${e.to}`;
    if (pr.has(p)) bad.push(`dup pair ${p}`);
    pr.add(p);
    if (!nid.has(e.from) || !nid.has(e.to)) bad.push(`dangling ${e.id}`);
  }
  for (const n of nodes) {
    const out = edges.filter((e) => e.from === n.id);
    if (n.kind === 'fork' && nodes.filter((j) => j.kind === 'join' && j.forkId === n.id).length !== 1) bad.push(`fork ${n.id} without exactly one join`);
    if (n.kind === 'join' && !nodes.some((f) => f.kind === 'fork' && f.id === n.forkId)) bad.push(`join ${n.id} without fork`);
    if (n.kind === 'condition' && out.filter((e) => e.default).length !== 1) bad.push(`condition ${n.id} default count`);
    if (n.kind === 'approval' && out.length !== 1) bad.push(`approval ${n.id} out count ${out.length}`);
    if (!s.positions[n.id]) bad.push(`no position ${n.id}`);
  }
  return bad;
}

describe('nextCode', () => {
  it('new codes take the smallest free number and skip codes of the published version', () => {
    const s = line();
    expect(nextCode('approval', s.definition)).toBe('approval-1');
    const s2 = mk([...s.definition.nodes, appr('approval-1'), appr('approval-3')], s.definition.edges);
    expect(nextCode('approval', s2.definition)).toBe('approval-2');
    expect(nextCode('approval', s2.definition, ['approval-2'])).toBe('approval-4');
    expect(nextCode('condition', s.definition)).toBe('cond-1');
    expect(nextCode('fork', s.definition)).toBe('fork-1');
    expect(nextCode('join', s.definition)).toBe('join-1');
    expect(nextCode('end', s.definition)).toBe('end-1');
  });
});

describe('insertOnEdge', () => {
  it('inserting an approval on an edge keeps the edge and its attributes on the first half', () => {
    const s = insertOnEdge(cond(), 'e3', 'approval', opts);
    const n = ids(s).find((i) => i.startsWith('approval-'))!;
    expect(edgeOf(s, 'c', n)).toMatchObject({ id: 'e3', order: 1, label: { 'zh-TW': '十萬以上' } });
    expect(pairs(s)).toContain(`${n}>x`);
    expect(localInvariants(s)).toEqual([]);
  });

  it('inserting a fork on an edge adds a paired join with two branches of one approval node each', () => {
    const s = insertOnEdge(line(), 'e2', 'fork', opts);
    const f = ids(s).find((i) => i.startsWith('fork-'))!;
    const j = s.definition.nodes.find((x) => x.kind === 'join')!;
    expect(j).toMatchObject({ id: expect.stringMatching(/^join-/), forkId: f, mode: 'all' });
    const branches = s.definition.edges.filter((e) => e.from === f).map((e) => e.to);
    expect(branches).toHaveLength(2);
    for (const b of branches) {
      expect(nodeOf(s, b).kind).toBe('approval');
      expect(s.definition.edges.filter((e) => e.from === b).map((e) => e.to)).toEqual([j.id]);
    }
    expect(pairs(s)).toContain(`mgr>${f}`);
    expect(pairs(s)).toContain(`${j.id}>fin`);
    expect(pairs(s)).not.toContain('mgr>fin');
    expect(localInvariants(s)).toEqual([]);
  });

  it('inserting a condition on an edge adds a default edge to the old target and one conditional edge through a new approval node', () => {
    const s = insertOnEdge(line(), 'e2', 'condition', opts);
    const c = ids(s).find((i) => i.startsWith('cond-'))!;
    const outs = conditionOutEdges(s.definition, c);
    expect(outs).toHaveLength(2);
    expect(outs[0]).toMatchObject({ order: 1, label: { 'zh-TW': '條件 1' } });
    expect(outs[0].when).toBeUndefined();
    expect(outs[1]).toMatchObject({ default: true, to: 'fin' });
    const x = outs[0].to;
    expect(nodeOf(s, x).kind).toBe('approval');
    expect(s.definition.edges.filter((e) => e.from === x).map((e) => e.to)).toEqual(['fin']);
    expect(pairs(s)).toContain(`mgr>${c}`);
    expect(localInvariants(s)).toEqual([]);
  });

  it('new approval nodes get the default name and the first-level manager source', () => {
    const s = insertOnEdge(line(), 'e2', 'approval', opts);
    const n = s.definition.nodes.find((x) => x.id.startsWith('approval-'))!;
    expect(n).toMatchObject({ kind: 'approval', name: { 'zh-TW': '簽核 1' }, approvers: { source: 'manager', level: 1 }, mode: 'any', editableFields: [], onEmpty: 'fallback' });
  });

  it('kinds that cannot be inserted on an edge leave the state alone', () => {
    const s = line();
    expect(insertOnEdge(s, 'e2', 'join', opts)).toBe(s);
    expect(insertOnEdge(s, 'e2', 'end', opts)).toBe(s);
    expect(insertOnEdge(s, 'nope', 'approval', opts)).toBe(s);
  });

  it('reserved codes of the published version are not reused', () => {
    const s = insertOnEdge(line(), 'e2', 'approval', { ...opts, reserved: ['approval-1'] });
    expect(ids(s)).toContain('approval-2');
    expect(ids(s)).not.toContain('approval-1');
  });

  it('does not mutate its input', () => {
    const s = line();
    const before = JSON.stringify(s);
    insertOnEdge(s, 'e2', 'fork', opts);
    expect(JSON.stringify(s)).toBe(before);
  });
});

describe('addNode', () => {
  it('adding a fork on blank canvas brings its join and two branches without outer edges', () => {
    const s = addNode(line(), 'fork', { x: 900, y: 400 }, opts);
    const f = ids(s).find((i) => i.startsWith('fork-'))!;
    const j = s.definition.nodes.find((x) => x.kind === 'join')!;
    expect(j.forkId).toBe(f);
    expect(s.definition.edges.filter((e) => e.from === f)).toHaveLength(2);
    expect(s.definition.edges.filter((e) => e.to === f)).toHaveLength(0);
    expect(s.definition.edges.filter((e) => e.from === j.id)).toHaveLength(0);
    expect(s.positions[f]).toEqual({ x: 900, y: 400 });
    expect(s.definition.edges).toHaveLength(4 + 4);
    // 簽核節點的一條出線在匯合，所以本地不變式成立
    expect(localInvariants(s)).toEqual([]);
  });

  it('adding a condition, approval or end on blank canvas adds just that node at the given place', () => {
    for (const kind of ['condition', 'approval', 'end'] as const) {
      const base = line();
      const s = addNode(base, kind, { x: 10, y: 20 }, opts);
      expect(s.definition.nodes).toHaveLength(base.definition.nodes.length + 1);
      expect(s.definition.edges).toHaveLength(base.definition.edges.length);
      const added = s.definition.nodes.at(-1)!;
      expect(added.kind).toBe(kind);
      expect(s.positions[added.id]).toEqual({ x: 10, y: 20 });
    }
  });

  it('join is not offered by addNode', () => {
    const s = line();
    expect(addNode(s, 'join', { x: 0, y: 0 }, opts)).toBe(s);
    expect(addNode(s, 'start', { x: 0, y: 0 }, opts)).toBe(s);
  });
});

describe('connect and canConnect', () => {
  it('canConnect refuses into start, out of end, self loops, duplicate pairs and a second out edge of an approval node', () => {
    const d = line().definition;
    expect(canConnect(d, 'mgr', 'start')).toBe('intoStart');
    expect(canConnect(d, 'end', 'mgr')).toBe('outOfEnd');
    expect(canConnect(d, 'mgr', 'mgr')).toBe('selfLoop');
    expect(canConnect(d, 'mgr', 'fin')).toBe('duplicate');
    expect(canConnect(d, 'mgr', 'ceo')).toBe('outLimit');
    expect(canConnect(d, 'start', 'ceo')).toBe('outLimit');
    expect(canConnect(d, 'nope', 'ceo')).toBe('missing');
  });

  it('canConnect allows what the node kinds allow', () => {
    const s = addNode(line(), 'approval', { x: 0, y: 0 }, opts);
    const lone = s.definition.nodes.at(-1)!.id;
    expect(canConnect(s.definition, lone, 'end')).toBeNull();
    expect(canConnect(s.definition, 'ceo', 'end')).toBe('duplicate');
    const c = cond().definition;
    expect(canConnect(c, 'c', 'end')).toBeNull();
    expect(canConnect(c, 'c', 'x')).toBe('duplicate');
  });

  it('connect adds an edge with the next free edge code and refuses what canConnect refuses', () => {
    const s = addNode(line(), 'approval', { x: 0, y: 0 }, opts);
    const lone = s.definition.nodes.at(-1)!.id;
    const t = connect(s, lone, 'end');
    expect(t.definition.edges.at(-1)).toMatchObject({ from: lone, to: 'end', id: 'e-1' });
    expect(connect(t, lone, 'end')).toBe(t);
  });

  it('a condition can take a second out edge, which gets the next order', () => {
    const s = connect(cond(), 'c', 'end');
    const outs = conditionOutEdges(s.definition, 'c');
    expect(outs.map((e) => e.order ?? 'default')).toEqual([1, 2, 'default']);
    expect(outs[1].when).toBeUndefined();
  });

  it('a condition edge dragged out gets the same "條件 n" label as one made by insertOnEdge; the default edge has none', () => {
    const s = addNode(line(), 'condition', { x: 0, y: 0 }, opts);
    const c = s.definition.nodes.at(-1)!.id;
    const t = connect(connect(connect(s, c, 'end', opts), c, 'ceo', opts), c, 'fin', opts);
    const outs = conditionOutEdges(t.definition, c);
    expect(outs.map((e) => e.label)).toEqual([{ 'zh-TW': '條件 1' }, { 'zh-TW': '條件 2' }, undefined]);
    const inserted = insertOnEdge(line(), 'e2', 'condition', opts);
    expect(inserted.definition.edges.find((e) => e.order === 1)!.label).toEqual(outs[0].label);
  });

  it('the first out edge of a fresh condition becomes its default edge', () => {
    const s = addNode(line(), 'condition', { x: 0, y: 0 }, opts);
    const c = s.definition.nodes.at(-1)!.id;
    const t = connect(connect(s, c, 'end'), c, 'ceo');
    const outs = conditionOutEdges(t.definition, c);
    expect(outs.map((e) => e.default === true)).toEqual([false, true]);
    expect(outs[0]).toMatchObject({ order: 1, to: 'ceo' });
  });
});

describe('removeNode', () => {
  it('removing a one-in one-out node bridges its neighbours and keeps the incoming edge\'s label and condition', () => {
    const s = cond();
    const withLabel = setEdge(line(), 'e2', { label: { 'zh-TW': '標籤' } });
    const t = removeNode(withLabel, 'fin');
    expect(ids(t)).not.toContain('fin');
    expect(edgeOf(t, 'mgr', 'ceo')).toMatchObject({ id: 'e2', label: { 'zh-TW': '標籤' } });
    expect(t.definition.edges.map((e) => e.id).sort()).toEqual(['e1', 'e2', 'e4']);
    expect(t.positions.fin).toBeUndefined();
    expect(localInvariants(t)).toEqual([]);
    // 條件的出線也一樣：條件 1 的線改指向下一個節點，條件與順序留下
    const u = removeNode(mk(
      [node('start', 'start'), node('c', 'condition'), appr('x'), appr('y'), node('end', 'end')],
      [edge('e1', 'start', 'c'), edge('e2', 'c', 'end', { default: true }), edge('e3', 'c', 'x', { order: 1, when: { all: [{ field: 1, op: 'gte', value: 1 }] } }), edge('e4', 'x', 'y'), edge('e5', 'y', 'end')],
    ), 'x');
    expect(edgeOf(u, 'c', 'y')).toMatchObject({ id: 'e3', order: 1, when: { all: [{ field: 1, op: 'gte', value: 1 }] } });
    expect(s.definition.edges).toHaveLength(5);
  });

  it('removing a node does not bridge when the bridge would duplicate an existing edge', () => {
    const t = removeNode(cond(), 'x');
    expect(pairs(t)).toEqual(['b>end', 'c>b', 'start>c']);
    // 條件 c 只剩預設線，本地不變式仍成立（驗證時才會指出出線不足）
    expect(localInvariants(t)).toEqual([]);
  });

  it('removing a notify node bridges like an approval node', () => {
    const s = mk([node('start', 'start'), appr('a'), node('n', 'notify'), node('end', 'end')], [edge('e1', 'start', 'a'), edge('e2', 'a', 'n'), edge('e3', 'n', 'end')]);
    expect(pairs(removeNode(s, 'n'))).toEqual(['a>end', 'start>a']);
  });

  it('removing a condition does not bridge', () => {
    const t = removeNode(cond(), 'c');
    expect(ids(t)).not.toContain('c');
    expect(pairs(t)).toEqual(['b>end', 'x>b']);
  });

  it('removing a fork with its join removes everything between them', () => {
    const s = insertOnEdge(line(), 'e2', 'fork', opts);
    const f = ids(s).find((i) => i.startsWith('fork-'))!;
    const t = removeNode(s, f, { withJoin: true });
    expect(ids(t)).toEqual(['start', 'mgr', 'fin', 'ceo', 'end']);
    expect(pairs(t)).toEqual(['ceo>end', 'fin>ceo', 'start>mgr']);
    for (const k of Object.keys(t.positions)) expect(ids(t)).toContain(k);
  });

  it('an unclosed fork block: removing it with its join only removes the nodes between them, not the rest of the main line or the end node', () => {
    // 編輯到一半：f 的分支 b 沒有接到匯合 j，直接走到 end；j 之後是 ceo → end
    const s = mk(
      [node('start', 'start'), node('f', 'fork'), appr('a'), appr('b'), node('j', 'join', { forkId: 'f', mode: 'all' }), appr('ceo'), node('end', 'end')],
      [edge('e1', 'start', 'f'), edge('e2', 'f', 'a'), edge('e3', 'a', 'j'), edge('e4', 'f', 'b'), edge('e5', 'b', 'end'), edge('e6', 'j', 'ceo'), edge('e7', 'ceo', 'end')],
    );
    const t = removeNode(s, 'f', { withJoin: true });
    expect(ids(t).sort()).toEqual(['b', 'ceo', 'end', 'start']);
    expect(pairs(t)).toEqual(['b>end', 'ceo>end']);
    for (const k of Object.keys(t.positions)) expect(ids(t)).toContain(k);
  });

  it('a fork whose branch ends in a dead end: the dead-end node is outside the block and stays', () => {
    const s = mk(
      [node('start', 'start'), node('f', 'fork'), appr('a'), appr('b'), node('j', 'join', { forkId: 'f', mode: 'all' }), node('end', 'end')],
      [edge('e1', 'start', 'f'), edge('e2', 'f', 'a'), edge('e3', 'a', 'j'), edge('e4', 'f', 'b'), edge('e5', 'j', 'end')],
    );
    const t = removeNode(s, 'f', { withJoin: true });
    expect(ids(t).sort()).toEqual(['b', 'end', 'start']);
  });

  it('removing a fork without its join keeps the rest', () => {
    const s = insertOnEdge(line(), 'e2', 'fork', opts);
    const f = ids(s).find((i) => i.startsWith('fork-'))!;
    const t = removeNode(s, f);
    expect(ids(t)).not.toContain(f);
    expect(t.definition.nodes.some((x) => x.kind === 'join')).toBe(true);
  });

  it('removing a nested fork block removes only the inner region', () => {
    let s = insertOnEdge(line(), 'e2', 'fork', opts);
    const outer = ids(s).find((i) => i.startsWith('fork-'))!;
    const branchEdge = s.definition.edges.find((e) => e.from === outer)!;
    s = insertOnEdge(s, branchEdge.id, 'fork', opts);
    const inner = ids(s).find((i) => i.startsWith('fork-') && i !== outer)!;
    const t = removeNode(s, inner, { withJoin: true });
    expect(t.definition.nodes.filter((x) => x.kind === 'fork').map((x) => x.id)).toEqual([outer]);
    expect(t.definition.nodes.filter((x) => x.kind === 'join')).toHaveLength(1);
  });

  it('start cannot be removed', () => {
    const s = line();
    expect(removeNode(s, 'start')).toBe(s);
    expect(removeNode(s, 'nope')).toBe(s);
  });

  it('removing an end node removes its edges', () => {
    const t = removeNode(line(), 'end');
    expect(ids(t)).not.toContain('end');
    expect(pairs(t)).not.toContain('ceo>end');
  });
});

describe('edges, nodes and positions', () => {
  it('removeEdge removes just that edge', () => {
    expect(pairs(removeEdge(line(), 'e2'))).toEqual(['ceo>end', 'fin>ceo', 'start>mgr']);
  });

  it('setNode and setEdge merge a patch and drop undefined values; moveNode sets the position', () => {
    let s = setNode(line(), 'mgr', { name: { 'zh-TW': '新名' }, dueHours: 8 });
    expect(nodeOf(s, 'mgr')).toMatchObject({ name: { 'zh-TW': '新名' }, dueHours: 8, kind: 'approval' });
    s = setNode(s, 'mgr', { dueHours: undefined });
    expect('dueHours' in nodeOf(s, 'mgr')).toBe(false);
    s = setEdge(s, 'e1', { label: { 'zh-TW': 'x' } });
    expect(s.definition.edges[0].label).toEqual({ 'zh-TW': 'x' });
    s = moveNode(s, 'mgr', { x: 5, y: 6 });
    expect(s.positions.mgr).toEqual({ x: 5, y: 6 });
    const same = line();
    expect(setNode(same, 'nope', { dueHours: 1 })).toBe(same);
    expect(moveNode(same, 'nope', { x: 1, y: 1 })).toBe(same);
  });

  it('reordering keeps the default edge last', () => {
    let s = connect(cond(), 'c', 'end');
    const second = conditionOutEdges(s.definition, 'c')[1].id;
    s = reorderConditionEdge(s, second, -1);
    let outs = conditionOutEdges(s.definition, 'c');
    expect(outs[0].id).toBe(second);
    expect(outs.map((e) => e.order ?? 'default')).toEqual([1, 2, 'default']);
    expect(outs.at(-1)!.default).toBe(true);
    // 往下一格回去；預設線與已到邊界的移動都不動
    s = reorderConditionEdge(s, second, 1);
    expect(conditionOutEdges(s.definition, 'c')[1].id).toBe(second);
    const same = s;
    expect(reorderConditionEdge(same, second, 1)).toBe(same);
    expect(reorderConditionEdge(same, conditionOutEdges(same.definition, 'c')[0].id, -1)).toBe(same);
    expect(reorderConditionEdge(same, 'e2', -1)).toBe(same);
    outs = conditionOutEdges(same.definition, 'c');
    expect(outs.at(-1)!.default).toBe(true);
  });

  it('renaming a code updates edges, forkId and positions', () => {
    const s = insertOnEdge(line(), 'e2', 'fork', opts);
    const f = ids(s).find((i) => i.startsWith('fork-'))!;
    const t = renameCode(s, f, 'split');
    expect(ids(t)).toContain('split');
    expect(ids(t)).not.toContain(f);
    expect(t.definition.nodes.find((x) => x.kind === 'join')!.forkId).toBe('split');
    expect(t.definition.edges.filter((e) => e.from === 'split')).toHaveLength(2);
    expect(t.definition.edges.some((e) => e.to === 'split')).toBe(true);
    expect(t.positions.split).toBeDefined();
    expect(t.positions[f]).toBeUndefined();
    expect(localInvariants(t)).toEqual([]);
  });

  it('renaming refuses a used, invalid or unknown code', () => {
    const s = line();
    expect(renameCode(s, 'mgr', 'fin')).toBe(s);
    expect(renameCode(s, 'mgr', 'Bad Code')).toBe(s);
    expect(renameCode(s, 'mgr', '1abc')).toBe(s);
    expect(renameCode(s, 'nope', 'ok')).toBe(s);
    expect(renameCode(s, 'mgr', 'mgr')).toBe(s);
  });
});

describe('relayout and topoOrder', () => {
  it('relayout gives every node a position', () => {
    const s = { ...cond(), positions: {} };
    const t = relayout(s);
    for (const n of t.definition.nodes) expect(t.positions[n.id]).toBeDefined();
  });

  it('relayout widens the gap after a column that has labeled edges', () => {
    const t = relayout(cond());
    const plain = layoutLeftToRight(cond().definition.nodes, cond().definition.edges);
    expect(t.positions.x.x - t.positions.c.x).toBeGreaterThan(plain.positions.x.x - plain.positions.c.x);
  });

  it('topoOrder lists nodes from start', () => {
    expect(topoOrder(line().definition)).toEqual(['start', 'mgr', 'fin', 'ceo', 'end']);
    const c = cond().definition;
    const order = topoOrder(c);
    expect(order[0]).toBe('start');
    expect(order.indexOf('c')).toBeLessThan(order.indexOf('x'));
    expect(order.indexOf('x')).toBeLessThan(order.indexOf('b'));
    expect(order.at(-1)).toBe('end');
    expect(order).toHaveLength(c.nodes.length);
  });

  it('topoOrder puts unreachable nodes after the reachable ones', () => {
    const s = addNode(line(), 'approval', { x: 0, y: 0 }, opts);
    const order = topoOrder(s.definition);
    expect(order.slice(0, 5)).toEqual(['start', 'mgr', 'fin', 'ceo', 'end']);
    expect(order).toHaveLength(6);
  });
});

interface SharedCase { id: string; errors: unknown[]; definition: GraphDefinition }
const valid = (cases.cases as unknown as SharedCase[]).filter((c) => c.errors.length === 0);

describe('invariants over the shared valid cases', () => {
  it.each(valid.map((c) => [c.id, c] as const))('every operation on every valid shared case keeps the local invariants (%s)', (_id, c) => {
    const positions: GraphState['positions'] = {};
    c.definition.nodes.forEach((n, i) => { positions[n.id] = { x: i * 224, y: 0 }; });
    const base: GraphState = { definition: structuredClone(c.definition), positions };
    expect(localInvariants(base)).toEqual([]);
    for (const e of base.definition.edges) {
      for (const kind of ['approval', 'condition', 'fork'] as const) {
        const t = insertOnEdge(base, e.id, kind, opts);
        expect(localInvariants(t), `insert ${kind} on ${e.id}`).toEqual([]);
        expect(t.definition.nodes.length).toBeGreaterThan(base.definition.nodes.length);
      }
    }
    for (const n of base.definition.nodes) {
      const ins = base.definition.edges.filter((e) => e.to === n.id).length;
      const outs = base.definition.edges.filter((e) => e.from === n.id).length;
      if ((n.kind === 'approval' || n.kind === 'notify') && ins === 1 && outs === 1) {
        const t = removeNode(base, n.id);
        expect(t.definition.nodes).toHaveLength(base.definition.nodes.length - 1);
        // 刪掉一個節點後，簽核節點仍是一條出線、沒有重複的線（條件節點的出線不足留給驗證）
        expect(localInvariants(t), `remove ${n.id}`).toEqual([]);
        expect(t.definition.edges.filter((e) => e.from === n.id || e.to === n.id)).toEqual([]);
      }
    }
  });
});

describe('patch values are copied at the source (U60 regression)', () => {
  const whenOf = (s: GraphState) => edgeOf(s, 'c', 'x').when as { all: { value: unknown }[] };

  it('undo after: edit condition, edit label, edit condition again goes back to the first condition value', () => {
    // ConditionEditor 之後還會原地修改它發出去的那個物件；復原堆疊不能跟它共用
    const row = { cond: { field: 1, op: 'eq' as const, value: 1 } };
    let h = createHistory(cond());
    h = push(h, setEdge(h.present, 'e3', { when: { all: [row.cond] } }));
    h = push(h, setEdge(h.present, 'e3', { label: { 'zh-TW': 'L' } }));
    row.cond.value = 2;
    h = push(h, setEdge(h.present, 'e3', { when: { all: [row.cond] } }));
    expect(whenOf(h.present).all[0].value).toBe(2);
    h = undo(h);
    h = undo(h);
    expect(whenOf(h.present).all[0].value).toBe(1);
  });

  it('a patch made of Vue reactive proxies neither throws nor stays shared', () => {
    const when = reactive({ all: [{ field: 1, op: 'eq', value: 5 }] });
    const next = setEdge(cond(), 'e3', { when: when as never });
    when.all[0].value = 6;
    expect(whenOf(next).all[0].value).toBe(5);
    expect(() => setNode(next, 'x', { name: reactive({ 'zh-TW': 'n' }) })).not.toThrow();
    expect(() => moveNode(setEdge(next, 'e3', { label: undefined }), 'x', { x: 1, y: 1 })).not.toThrow();
  });
});
