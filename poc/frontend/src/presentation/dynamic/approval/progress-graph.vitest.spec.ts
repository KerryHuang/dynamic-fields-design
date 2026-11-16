import { describe, expect, it } from 'vitest';
import type { ApprovalProgressEdge, ApprovalProgressNode, ApprovalProgressState, ApprovalStageView } from 'src/domain/dynamic/approval-types';
import { ADDSIGN_H, ADDSIGN_W, NODE_H, NODE_W } from 'src/domain/dynamic/approval-graph-layout';
import enUS from 'src/i18n/en-US.json';
import zhTW from 'src/i18n/zh-TW.json';
import { stageColor, stageIcon } from './approval-display';
import { buildProgressGraph, EDGE_LABEL_MAX_PX, focusNodeId, graphHeight, initialViewport, stateView, truncateEdgeLabel, viewportTransform } from './progress-graph';

const lookup = (messages: Record<string, unknown>) => (key: string) => key.split('.').reduce<unknown>((o, k) => (o as Record<string, unknown> | undefined)?.[k], messages) as string ?? key;
const t = lookup(zhTW);
const tEn = lookup(enUS);

const node = (id: string, kind: ApprovalProgressNode['kind'], state: ApprovalProgressState, p: Partial<ApprovalProgressNode> = {}): ApprovalProgressNode => ({ id, kind, state, ...p });
const edge = (id: string, from: string, to: string, traversed = false, p: Partial<ApprovalProgressEdge> = {}): ApprovalProgressEdge => ({ id, from, to, traversed, ...p });
const stage = (id: number, seq: number, p: Partial<ApprovalStageView> = {}): ApprovalStageView => ({
  id, seq, stageCode: null, name: { 'zh-TW': 's' }, mode: 'all', status: 'active', skipReason: null, dueTime: null, tasks: [], editableFieldKeys: [], ...p,
});
const ctx = { locale: 'zh-TW', t, now: Date.parse('2026-11-16T12:00:00Z') };

/** start → 主管 → fork → (法務 | 財務) → join(any) → end */
const parallel = () => ({
  nodes: [
    node('s', 'start', 'passed'), node('m', 'approval', 'passed', { name: { 'zh-TW': '主管' }, runIds: [1] }), node('f', 'fork', 'passed'),
    node('l', 'approval', 'active', { name: { 'zh-TW': '法務' }, runIds: [2] }), node('c', 'approval', 'active', { name: { 'zh-TW': '財務' }, runIds: [3] }),
    node('j', 'join', 'active', { joinMode: 'any' }), node('e', 'end', 'notReached'),
  ],
  edges: [edge('1', 's', 'm', true), edge('2', 'm', 'f', true), edge('3', 'f', 'l', true), edge('4', 'f', 'c', true), edge('5', 'l', 'j'), edge('6', 'c', 'j'), edge('7', 'j', 'e')],
});
const stagesOf = [stage(1, 1, { status: 'approved' }), stage(2, 2), stage(3, 3)];

describe('stateView', () => {
  it('maps every state to the timeline colour, icon and text key', () => {
    expect(stateView('passed', 'approval')).toMatchObject({ color: stageColor('approved'), icon: stageIcon('approved'), textKey: 'approval.stageStatus.approved', frame: 'solid' });
    expect(stateView('passed', 'condition').textKey).toBe('approval.graph.state.passed');
    expect(stateView('returned', 'approval')).toMatchObject({ color: stageColor('returned'), icon: stageIcon('returned'), textKey: 'approval.stageStatus.returned' });
    expect(stateView('skipped', 'approval')).toMatchObject({ color: stageColor('skipped'), icon: stageIcon('skipped'), frame: 'dashed' });
    expect(stateView('ended', 'approval')).toMatchObject({ color: stageColor('closed'), icon: stageIcon('closed'), textKey: 'approval.stageStatus.closed', strike: false, frame: 'solid' });
    expect(stateView('cancelled', 'approval')).toMatchObject({ color: stageColor('closed'), icon: stageIcon('closed'), textKey: 'approval.graph.state.cancelled', strike: true });
    expect(stateView('notReached', 'approval')).toMatchObject({ color: null, icon: null, textKey: null, dim: true });
    expect(stateView('changed', 'approval')).toMatchObject({ color: 'muted', icon: 'history', textKey: 'approval.graph.state.changed' });
  });

  it('active uses warning and hourglass like the timeline', () => {
    expect(stateView('active', 'approval')).toMatchObject({ color: 'warning', icon: 'hourglass_top', textKey: 'approval.stageStatus.active' });
    expect(stageColor('active')).toBe('warning');
    expect(stateView('active', 'join').textKey).toBe('approval.graph.state.waiting');
  });

  it('hidden nodes get no state text, no icon and a neutral frame', () => {
    expect(stateView('hidden', 'approval')).toMatchObject({ color: null, icon: null, textKey: null, frame: 'neutral', dim: false });
    // 不認得的狀態（較新的後端）同樣中性，不丟例外
    expect(stateView('someNewState', 'approval')).toMatchObject({ color: null, icon: null, textKey: null });
  });

  it('a queued add-sign reads as waiting and not reached', () => {
    expect(stateView('notReached', 'addSign')).toMatchObject({ dim: true, textKey: 'approval.stageStatus.queued' });
  });

  it('every text key exists in all four languages', async () => {
    const th = (await import('src/i18n/th.json')).default as Record<string, unknown>;
    const vi = (await import('src/i18n/vi.json')).default as Record<string, unknown>;
    const states = ['passed', 'active', 'returned', 'skipped', 'cancelled', 'ended', 'notReached', 'changed', 'hidden'];
    for (const m of [zhTW, enUS, th, vi]) {
      const get = lookup(m as Record<string, unknown>);
      for (const kind of ['approval', 'addSign', 'join', 'end']) for (const s of states) {
        const key = stateView(s, kind).textKey;
        if (key) expect(get(key), key).not.toBe(key);
      }
    }
  });
});

describe('buildProgressGraph', () => {
  it('changed nodes read as handled with later changes', () => {
    const g = buildProgressGraph({ nodes: [node('s', 'start', 'hidden'), node('m', 'approval', 'changed', { name: { 'zh-TW': '財務' }, runIds: [1] })], edges: [edge('1', 's', 'm')] }, [], ctx);
    const d = g.nodes[1].data!;
    expect(d.statusText).toBe('已處理（之後有變動）');
    expect(d.ariaLabel).toBe('財務 · 已處理（之後有變動）');
    expect(g.nodes[0].data!.statusText).toBe('');
    expect(g.nodes[0].data!.ariaLabel).toBe('開始');
  });

  it('traversed edges are solid and thick, others thin and dashed', () => {
    const g = buildProgressGraph(parallel(), stagesOf, ctx);
    const cls = Object.fromEntries(g.edges.map((e) => [e.id, e.class]));
    expect(cls['1']).toContain('pg-edge-on');
    expect(cls['5']).toContain('pg-edge-off');
    expect(g.edges.every((e) => e.type === 'progress' && e.focusable === false)).toBe(true);
  });

  it('every edge is hidden from the accessibility tree: no Vue Flow English text with internal node ids', () => {
    const g = buildProgressGraph(parallel(), stagesOf, ctx);
    expect(g.edges).toHaveLength(7);
    for (const e of g.edges) {
      expect(e.ariaLabel).toBeNull();
      expect(e.domAttributes).toEqual({ 'aria-hidden': 'true' });
    }
  });

  it('edges bend in a column gap: branching edges after the source, the others before the target', () => {
    const g = buildProgressGraph(parallel(), stagesOf, ctx);
    const pos = Object.fromEntries(g.nodes.map((n) => [n.id, n.position]));
    const bend = Object.fromEntries(g.edges.map((e) => [e.id, (e.data as { bendX: number }).bendX]));
    expect(bend['3']).toBe(pos.f.x + NODE_W + 28); // f 有兩條出線
    expect(bend['4']).toBe(pos.f.x + NODE_W + 28);
    expect(bend['5']).toBe(pos.j.x - 28); // l → j
    expect(bend['2']).toBe(pos.f.x - 28); // m → f
  });

  it('a node that is not reached is read as not reached; the screen shows no text for it', () => {
    const d = buildProgressGraph(parallel(), stagesOf, ctx).nodes.find((n) => n.id === 'e')!.data!;
    expect(d.statusText).toBe('');
    expect(d.ariaLabel).toBe('結束 · 未走到');
  });

  it('add-sign nodes come from the API as nodes and use the small size', () => {
    const graph = {
      nodes: [node('s', 'start', 'passed'), node('m', 'approval', 'passed', { runIds: [1] }), node('as-m-1', 'addSign', 'active', { hostId: 'm', runIds: [2] }), node('e', 'end', 'notReached')],
      edges: [edge('1', 's', 'm', true), edge('as-m-1-in', 'm', 'as-m-1', true), edge('2', 'as-m-1', 'e')],
    };
    const g = buildProgressGraph(graph, [stage(1, 1, { status: 'approved' }), stage(2, 2)], ctx);
    const small = g.nodes.find((n) => n.id === 'as-m-1')!;
    expect(small.type).toBe('addSign');
    expect(small.data).toMatchObject({ small: true, width: ADDSIGN_W, height: ADDSIGN_H, label: '加簽' });
    expect(small.style).toEqual({ width: `${ADDSIGN_W}px`, height: `${ADDSIGN_H}px` });
    const big = g.nodes.find((n) => n.id === 'm')!;
    expect(big.type).toBe('progress');
    expect(big.data).toMatchObject({ width: NODE_W, height: NODE_H });
  });

  it('unnamed condition, fork and join fall back to the kind label; unknown kinds get a default style', () => {
    const graph = {
      nodes: [node('s', 'start', 'passed'), node('c', 'condition', 'passed'), node('f', 'fork', 'passed'), node('j', 'join', 'passed', { joinMode: 'all' }), { id: 'x', kind: 'robot' as never, state: 'passed' as const }],
      edges: [edge('1', 's', 'c'), edge('2', 'c', 'f'), edge('3', 'f', 'j'), edge('4', 'j', 'x')],
    };
    const g = buildProgressGraph(graph, [], ctx);
    expect(g.nodes.map((n) => n.data!.label)).toEqual(['開始', '條件', '分支', '匯合', 'robot']);
    expect(g.nodes[4].data!.kindIcon).toBe('help_outline');
    expect(g.nodes[3].data!.joinModeText).toBe('全部到齊');
    expect(g.nodes[3].data!.ariaLabel).toBe('匯合 · 全部到齊 · 已通過');
  });

  it('names resolve by locale', () => {
    const graph = { nodes: [node('m', 'approval', 'active', { name: { 'zh-TW': '主管', 'en-US': 'Manager' } })], edges: [] };
    expect(buildProgressGraph(graph, [], { ...ctx, locale: 'en-US', t: tEn }).nodes[0].data!.label).toBe('Manager');
    expect(buildProgressGraph(graph, [], { ...ctx, locale: 'th' }).nodes[0].data!.label).toBe('主管');
  });

  it('edge labels resolve by locale and unlabeled edges have none', () => {
    const graph = {
      nodes: [node('c', 'condition', 'passed'), node('a', 'end', 'passed'), node('b', 'end', 'notReached')],
      edges: [edge('1', 'c', 'a', true, { label: { 'zh-TW': '金額大', 'en-US': 'Large' } }), edge('2', 'c', 'b')],
    };
    expect(buildProgressGraph(graph, [], ctx).edges.map((e) => e.label)).toEqual(['金額大', undefined]);
    expect(buildProgressGraph(graph, [], { ...ctx, locale: 'en-US' }).edges[0].label).toBe('Large');
    expect('label' in buildProgressGraph(graph, [], ctx).edges[1]).toBe(false);
  });

  it('long edge labels are cut to the column gap and ended with an ellipsis; the full text stays in the data', () => {
    const full = '金額大於十萬且非急件';
    const graph = { nodes: [node('c', 'condition', 'passed'), node('a', 'end', 'passed')], edges: [edge('1', 'c', 'a', true, { label: { 'zh-TW': full } })] };
    const e = buildProgressGraph(graph, [], ctx).edges[0];
    expect(e.label).toBe('金額大…');
    expect((e.data as { fullLabel: string }).fullLabel).toBe(full);
    const short = buildProgressGraph({ ...graph, edges: [edge('1', 'c', 'a', true, { label: { 'zh-TW': '稅率' } })] }, [], ctx).edges[0];
    expect(short.label).toBe('稅率');
    expect((short.data as { fullLabel: string }).fullLabel).toBe('稅率');
  });

  it('overdue active node shows the overdue badge data on the status row', () => {
    const graph = { nodes: [node('m', 'approval', 'active', { runIds: [1] }), node('n', 'approval', 'active', { runIds: [2] })], edges: [] };
    const g = buildProgressGraph(graph, [stage(1, 1, { dueTime: '2026-11-16T08:00:00Z' }), stage(2, 2, { dueTime: '2026-11-17T08:00:00Z' })], ctx);
    expect(g.nodes[0].data).toMatchObject({ overdue: true, overdueText: '逾期' });
    expect(g.nodes[1].data!.overdue).toBe(false);
  });

  it('a node whose latest run is no longer active is not overdue, even past its due time', () => {
    const graph = { nodes: [node('m', 'approval', 'passed', { runIds: [1] })], edges: [] };
    const g = buildProgressGraph(graph, [stage(1, 1, { status: 'approved', dueTime: '2026-11-16T08:00:00Z' })], ctx);
    expect(g.nodes[0].data!.overdue).toBe(false);
    expect(g.nodes[0].data!.overdueText).toBe('');
  });

  it('a node without runIds is read as an empty list', () => {
    const g = buildProgressGraph({ nodes: [node('m', 'approval', 'active')], edges: [] }, [], ctx);
    expect(g.nodes[0].data!.overdue).toBe(false);
  });

  it('only approval and add-sign nodes that are shown and have runs are clickable; they carry their run ids', () => {
    const g = {
      nodes: [
        node('s', 'start', 'passed'), node('a', 'approval', 'active', { runIds: [1, 4] }), node('as', 'addSign', 'notReached', { runIds: [5], hostId: 'a' }),
        node('c', 'condition', 'passed'), node('f', 'fork', 'passed'), node('j', 'join', 'active', { joinMode: 'all' }), node('n', 'notify', 'passed'),
        node('h', 'approval', 'hidden'), node('x', 'approval', 'changed', { runIds: [2] }), node('u', 'approval', 'notReached'), node('e', 'end', 'notReached'),
      ],
      edges: [],
    };
    const byId = Object.fromEntries(buildProgressGraph(g, [], ctx).nodes.map((n) => [n.id, n.data]));
    expect(Object.fromEntries(Object.entries(byId).map(([k, v]) => [k, v!.clickable]))).toEqual({
      s: false, a: true, as: true, c: false, f: false, j: false, n: false, h: false, x: true, u: false, e: false,
    });
    expect(byId.a!.runIds).toEqual([1, 4]);
    expect(byId.as!.runIds).toEqual([5]);
    expect(byId.s!.runIds).toEqual([]);
  });

  it('the layout runs left to right with parallel branches stacked', () => {
    const g = buildProgressGraph(parallel(), stagesOf, ctx);
    const pos = Object.fromEntries(g.nodes.map((n) => [n.id, n.position]));
    expect(pos.l.x).toBe(pos.c.x);
    expect(pos.l.y).toBeLessThan(pos.c.y);
    expect(pos.j.x).toBeGreaterThan(pos.l.x);
  });
});

describe('truncateEdgeLabel', () => {
  const width = (text: string) => [...text].reduce((w, c) => w + ((c.codePointAt(0) ?? 0) >= 0x2e80 ? 11 : 6.5), 0);
  it('keeps text that fits the gap', () => {
    expect(EDGE_LABEL_MAX_PX).toBe(48);
    expect(truncateEdgeLabel('稅率')).toBe('稅率');
    expect(truncateEdgeLabel('金額大於')).toBe('金額大於'); // 44px
    expect(truncateEdgeLabel('Large')).toBe('Large'); // 32.5px
  });
  it('cuts CJK and Latin text by estimated width and adds an ellipsis that still fits', () => {
    expect(truncateEdgeLabel('稅率一以上')).toBe('稅率一…');
    expect(truncateEdgeLabel('Amount above 100k')).toBe('Amoun…');
    for (const text of ['金額大於十萬且非急件', 'Amount above 100k and not urgent', '大 Large amount']) {
      const cut = truncateEdgeLabel(text);
      expect(cut.endsWith('…')).toBe(true);
      expect(width(cut)).toBeLessThanOrEqual(EDGE_LABEL_MAX_PX);
    }
  });
});

describe('focus node', () => {
  it('a node re-run after a return is ordered by its current (latest) run, not its oldest run', () => {
    const g = parallel();
    g.nodes[3].runIds = [2, 6]; // 法務：先前被退回，目前是第 6 筆執行
    const stages = [stage(1, 1), stage(2, 2), stage(3, 4), stage(6, 7)];
    expect(focusNodeId(g, stages, buildProgressGraph(g, [], ctx).layout)).toBe('c');
  });
  it('picks the earliest active node by its run seq', () => {
    const g = parallel();
    expect(focusNodeId(g, [stage(1, 1), stage(2, 5), stage(3, 4)], buildProgressGraph(g, [], ctx).layout)).toBe('c');
  });
  it('a waiting join (no runs) comes after active nodes that have runs', () => {
    const g = parallel();
    g.nodes[3].state = 'passed';
    g.nodes[4].state = 'passed';
    expect(focusNodeId(g, stagesOf, buildProgressGraph(g, [], ctx).layout)).toBe('j');
  });
  it('with nothing active it is the last reached node, ignoring cancelled and not reached', () => {
    const g = {
      nodes: [node('s', 'start', 'passed'), node('m', 'approval', 'returned', { runIds: [1] }), node('x', 'approval', 'cancelled'), node('e', 'end', 'notReached')],
      edges: [edge('1', 's', 'm', true), edge('2', 'm', 'x'), edge('3', 'x', 'e')],
    };
    expect(focusNodeId(g, [stage(1, 1, { status: 'returned' })], buildProgressGraph(g, [], ctx).layout)).toBe('m');
  });
  it('is null when nothing was reached', () => {
    const g = { nodes: [node('s', 'start', 'hidden')], edges: [] };
    expect(focusNodeId(g, [], buildProgressGraph(g, [], ctx).layout)).toBeNull();
    expect(buildProgressGraph(g, [], ctx).focus).toBeNull();
  });
});

describe('initialViewport', () => {
  const layout = (nodes: number) => ({ width: nodes * NODE_W + (nodes - 1) * 56, height: NODE_H });

  it('fits the whole graph when the scale is at least 0.8', () => {
    const l = layout(5); // 1064 寬
    const p = initialViewport(l, 1000);
    expect(p.zoom).toBeCloseTo((1000 - 32) / l.width, 5);
    expect(p.zoom).toBeGreaterThanOrEqual(0.8);
    expect(p.center).toEqual({ x: l.width / 2, y: l.height / 2 });
  });

  it('never enlarges: a small graph in a wide box stays at 1', () => {
    expect(initialViewport(layout(3), 1800).zoom).toBe(1);
  });

  it('keeps 0.8 and centres on the earliest active node otherwise', () => {
    const l = layout(14);
    const focus = { x: 1500, y: 42 };
    const p = initialViewport(l, 1200, 'inline', { focus });
    expect(p.zoom).toBe(0.8);
    expect(p.center).toEqual(focus);
    const tr = viewportTransform(p, 1200, 120);
    expect(tr).toEqual({ x: 600 - 1500 * 0.8, y: 60 - 42 * 0.8, zoom: 0.8 });
    // 內容放得進框時垂直置中，不跟著節點偏移
    const tall = { width: 3080, height: 300 };
    expect(initialViewport(tall, 1200, 'inline', { focus: { x: 1500, y: 40 }, maxHeight: 600 }).center.y).toBe(150);
    expect(initialViewport(tall, 1200, 'inline', { focus: { x: 1500, y: 40 }, maxHeight: 200 }).center.y).toBe(40);
  });

  it('does not scroll past the left and right edge of the graph', () => {
    const l = layout(14);
    const left = initialViewport(l, 1200, 'inline', { focus: { x: 84, y: 42 } });
    expect(viewportTransform(left, 1200, 120).x).toBeCloseTo(16, 5); // 圖的左邊距 16px
    const right = initialViewport(l, 1200, 'inline', { focus: { x: l.width - 84, y: 42 } });
    expect(viewportTransform(right, 1200, 120).x + l.width * 0.8).toBeCloseTo(1200 - 16, 5);
    // 沒有可對準的節點：從最左邊看起
    expect(viewportTransform(initialViewport(l, 1200, 'inline'), 1200, 120).x).toBeCloseTo(16, 5);
  });

  it('centres on the last reached node when nothing is active', () => {
    const g = {
      nodes: [node('s', 'start', 'passed'), node('m', 'approval', 'returned', { runIds: [1] }), node('e', 'end', 'notReached')],
      edges: [edge('1', 's', 'm', true), edge('2', 'm', 'e')],
    };
    const b = buildProgressGraph(g, [stage(1, 1, { status: 'returned' })], ctx);
    expect(b.focus).toEqual({ x: b.layout.positions.m.x + NODE_W / 2, y: b.layout.positions.m.y + NODE_H / 2 });
  });

  it('minZoom drops below 0.2 when the full graph needs it', () => {
    const l = layout(30);
    const p = initialViewport(l, 1200, 'inline');
    expect(p.fit).toBeLessThan(0.2);
    expect(p.minZoom).toBe(p.fit);
    expect(initialViewport(layout(14), 1200).minZoom).toBeGreaterThan(0.1);
    expect(initialViewport(layout(14), 3000).minZoom).toBe(0.2); // 全圖比例大於 0.2 時維持 0.2
  });

  it('minZoom also reaches the full height when the box height is capped', () => {
    const tall = { width: 400, height: 3000 };
    const p = initialViewport(tall, 800, 'inline', { maxHeight: 500 });
    expect(p.minZoom).toBeCloseTo((500 - 32) / 3000, 5);
  });

  it('thumbnail of a 14-stage flow at 358px still fits', () => {
    const l = layout(16); // 開始＋14 關＋結束
    const p = initialViewport(l, 358, 'thumb');
    expect(p.zoom).toBeLessThan(0.2);
    expect(p.zoom * l.width).toBeLessThanOrEqual(358 - 32 + 0.001);
    expect(p.minZoom).toBeLessThanOrEqual(p.zoom);
    expect(p.center).toEqual({ x: l.width / 2, y: l.height / 2 });
  });
});

describe('graphHeight', () => {
  it('is clamped between 160px and 60% of the viewport', () => {
    expect(graphHeight({ width: 500, height: NODE_H }, 1000, 'inline', 900)).toBe(160); // 84 + 32 = 116 → 下限
    expect(graphHeight({ width: 500, height: 200 }, 1000, 'inline', 900)).toBe(232);
    expect(graphHeight({ width: 500, height: 2000 }, 1000, 'inline', 900)).toBe(540); // 上限 60%
    expect(graphHeight({ width: 500, height: 2000 }, 1000, 'inline', 200)).toBe(160); // 視窗很矮時下限優先
  });
  it('uses the initial scale: a wide graph at 0.8 is as tall as 0.8 of its content', () => {
    expect(graphHeight({ width: 5000, height: 300 }, 1000, 'inline', 1000)).toBeCloseTo(300 * 0.8 + 32, 5);
  });
  it('the thumbnail is 180px', () => {
    expect(graphHeight({ width: 5000, height: 300 }, 358, 'thumb', 800)).toBe(180);
  });
});
