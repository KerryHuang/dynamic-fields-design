import { describe, expect, it } from 'vitest';
import type { ApprovalFlow, GraphDefinition } from 'src/domain/dynamic/approval-flow-types';
import { edgeBendX, layoutLeftToRight, NODE_H, NODE_W } from 'src/domain/dynamic/approval-graph-layout';
import { mapGraphIssues } from 'src/domain/dynamic/approval-graph-errors';
import zhTW from 'src/i18n/zh-TW.json';
import type { TrialOverlay } from './designer-trial';
import { initialState, newFlowState, sourceOf, toVueFlow, FLOW_SELECTION, type GraphContext } from './designer-graph';

const msg = (k: string, p?: Record<string, unknown>) => {
  const v = k.split('.').reduce<unknown>((o, s) => (o as Record<string, unknown> | undefined)?.[s], zhTW as unknown);
  return typeof v === 'string' ? v.replace(/\{(\w+)\}/g, (_, n: string) => String(p?.[n])) : k;
};
const def = (): GraphDefinition => ({
  shape: 'graph', stages: [],
  nodes: [
    { id: 's', kind: 'start' },
    { id: 'c', kind: 'condition' },
    { id: 'a', kind: 'approval', name: { 'zh-TW': '財務' }, approvers: { source: 'manager', level: 2 }, mode: 'all', when: { all: [{ field: 1, op: 'gte', value: 1 }] } },
    { id: 'e', kind: 'end' },
  ],
  edges: [
    { id: 'e1', from: 's', to: 'c' },
    { id: 'e2', from: 'c', to: 'e', default: true },
    { id: 'e3', from: 'c', to: 'a', order: 1 },
    { id: 'e4', from: 'a', to: 'e' },
  ],
});
const ctx = (over: Partial<GraphContext> = {}): GraphContext => ({ locale: 'zh-TW', t: msg, fields: [], issues: null, selection: FLOW_SELECTION, ...over });
const flow = (p: Partial<ApprovalFlow>): ApprovalFlow => ({ key: 'k', name: {}, toOptionId: 1, priority: 1, active: false, used: false, pendingCount: 0, moduleKey: 'm', ...p });

describe('designer-graph', () => {
  it('a flow without layout is laid out left to right', () => {
    const s = initialState(def(), null);
    const auto = layoutLeftToRight(def().nodes, def().edges);
    expect(Object.keys(s.positions).sort()).toEqual(['a', 'c', 'e', 's']);
    expect(s.positions.s.x).toBeLessThan(s.positions.c.x);
    expect(s.positions.c.x).toBeLessThan(s.positions.e.x);
    expect(auto.positions.s.x).toBe(0);
  });

  it('saved positions are kept and only the missing nodes are laid out', () => {
    const s = initialState(def(), { nodes: { s: { x: 5, y: 6 }, c: { x: 300, y: 6 }, e: { x: 900, y: 6 } } });
    expect(s.positions.s).toEqual({ x: 5, y: 6 });
    expect(s.positions.a).toBeDefined();
  });

  it('loads the draft when there is one, else the converted graph, else the published graph', () => {
    const draft = { ...def(), when: null };
    expect(sourceOf(flow({ draft: { definition: draft }, graph: def(), draftRev: 'r' }))?.definition).toBe(draft);
    const graph = def();
    expect(sourceOf(flow({ graph, draftRev: 'r' }))?.definition).toBe(graph);
    expect(sourceOf(flow({ definition: def(), draftRev: 'after-publish' }))?.definition.nodes).toHaveLength(4);
    expect(sourceOf(flow({}))).toBeNull();
  });

  it('draft layout wins over the published layout', () => {
    const src = sourceOf(flow({ draft: { definition: def(), layout: { nodes: { s: { x: 1, y: 1 } } } }, layout: { nodes: { s: { x: 9, y: 9 } } } }));
    expect(src?.layout?.nodes.s).toEqual({ x: 1, y: 1 });
  });

  it('node size comes from the layout constants', () => {
    const { nodes } = toVueFlow(initialState(def(), null), ctx(), { draggable: true });
    for (const n of nodes) expect(n.style).toEqual({ width: `${NODE_W}px`, height: `${NODE_H}px` });
    expect(nodes[0].data).toMatchObject({ width: NODE_W, height: NODE_H });
  });

  it('node shows kind, name, approver summary and skip-condition badge', () => {
    const { nodes } = toVueFlow(initialState(def(), null), ctx(), { draggable: false });
    const a = nodes.find((n) => n.id === 'a')!;
    expect(a.data).toMatchObject({ label: '財務', icon: 'person', skip: true });
    expect(a.data!.summary).toContain('第 2 層');
    expect(a.data!.summary).toContain('會簽');
    expect(a.draggable).toBe(false);
    expect(nodes.find((n) => n.id === 'c')!.data!.summary).toBe('2 條出線');
    expect(nodes.find((n) => n.id === 's')!.data!.label).toBe('開始');
  });

  it('condition edges show their summary: default is "other", unset is a grey hint (not an error before validating)', () => {
    const { edges } = toVueFlow(initialState(def(), null), ctx(), { draggable: true });
    expect(edges.find((e) => e.id === 'e2')!.label).toBe('其他');
    const e3 = edges.find((e) => e.id === 'e3')!;
    expect(e3.label).toBe('未設定條件');
    expect(e3.data!.hint).toBe(true);
    expect(e3.class).toContain('de-edge-hint');
    expect(e3.class).not.toContain('de-edge-error');
  });

  it('designer edges bend where edgeBendX says', () => {
    const s = initialState(def(), null);
    const { edges } = toVueFlow(s, ctx(), { draggable: true });
    const p = s.positions;
    // c has two out edges: branching bend after the start slot; e1 is a plain edge: bend before the target slot
    expect(edges.find((e) => e.id === 'e1')!.data!.bendX).toBe(edgeBendX({ x: p.s.x }, { x: p.c.x }, false));
    expect(edges.find((e) => e.id === 'e3')!.data!.bendX).toBeGreaterThan(p.c.x + NODE_W);
    expect(edges.find((e) => e.id === 'e3')!.data!.bendX).toBeLessThan(p.a.x);
  });

  it('errors and warnings are counted per node and edge and mark the edge class', () => {
    const issues = mapGraphIssues({ 'definition.nodes.a.approvers': ['x'], 'definition.edges.e3.when': ['請設定條件'] }, [{ key: 'definition.nodes.a', code: 'W1', message: 'w' }], def());
    const { nodes, edges } = toVueFlow(initialState(def(), null), ctx({ issues, selection: { kind: 'node', id: 'a' } }), { draggable: true });
    expect(nodes.find((n) => n.id === 'a')!.data).toMatchObject({ errors: 1, warnings: 1, selected: true });
    expect(edges.find((e) => e.id === 'e3')!.class).toContain('de-edge-error');
  });

  it('a trial overlay lands on nodes and edges: data, edge class and the screen-reader state', () => {
    const trial: TrialOverlay = {
      nodes: {
        s: { state: 'path', approvers: [], pick: false, fallback: false, problem: null, anyGroup: null },
        c: { state: 'path', approvers: [], pick: false, fallback: false, problem: null, anyGroup: null },
        a: { state: 'path', approvers: ['王小明', '李小華'], pick: false, fallback: false, problem: null, anyGroup: null },
        e: { state: 'off', approvers: [], pick: false, fallback: false, problem: null, anyGroup: null },
      },
      edges: { e1: 'path', e2: 'off', e3: 'path', e4: 'path' },
    };
    const { nodes, edges } = toVueFlow(initialState(def(), null), ctx({ trial }), { draggable: true });
    const a = nodes.find((n) => n.id === 'a')!;
    expect(a.data!.trial).toBe(trial.nodes.a);
    expect(a.data!.ariaLabel).toContain('預計會走到');
    expect(a.data!.ariaLabel).toContain('王小明、李小華');
    expect(nodes.find((n) => n.id === 'e')!.data!.ariaLabel).toContain('預計不會走到');
    expect(edges.find((e) => e.id === 'e3')!.class).toContain('de-edge-trial-path');
    expect(edges.find((e) => e.id === 'e2')!.class).toContain('de-edge-trial-off');
    expect(edges.find((e) => e.id === 'e2')!.data!.trial).toBe('off');
  });

  it('without a trial nothing is added to nodes and edges', () => {
    const { nodes, edges } = toVueFlow(initialState(def(), null), ctx(), { draggable: true });
    for (const n of nodes) expect(n.data).not.toHaveProperty('trial');
    for (const e of edges) { expect(e.class).not.toContain('trial'); expect(e.data).not.toHaveProperty('trial'); }
  });

  it('a new flow starts as start, approval 1, end (U51)', () => {
    const s = newFlowState();
    expect(s.definition.nodes.map((n) => n.kind)).toEqual(['start', 'approval', 'end']);
    expect(s.definition.nodes[1]).toMatchObject({ approvers: { source: 'manager', level: 1 }, mode: 'any' });
    expect(s.definition.nodes[1].name).toMatchObject({ 'zh-TW': '簽核 1', 'en-US': 'Approval 1' });
    expect(Object.keys(s.positions)).toHaveLength(3);
  });
});
