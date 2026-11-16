import { describe, expect, it } from 'vitest';
import type { GraphDefinition } from 'src/domain/dynamic/approval-flow-types';
import type { ApprovalPreview, PreviewStage } from 'src/domain/dynamic/approval-types';
import { trialOverlay } from './designer-trial';

const appr = (id: string) => ({ id, kind: 'approval' as const, name: { 'zh-TW': id }, approvers: { source: 'manager' as const, level: 1 }, mode: 'any' as const, editableFields: [], onEmpty: 'fallback' as const });
// s → c ─(大額)→ a1 → e / c ─(其他)→ a2 → e
const def = (): GraphDefinition => ({
  shape: 'graph', stages: [],
  nodes: [{ id: 's', kind: 'start' }, { id: 'c', kind: 'condition' }, appr('a1'), appr('a2'), { id: 'e', kind: 'end' }],
  edges: [
    { id: 'e1', from: 's', to: 'c' }, { id: 'e2', from: 'c', to: 'a1', order: 1 }, { id: 'e3', from: 'c', to: 'a2', default: true },
    { id: 'e4', from: 'a1', to: 'e' }, { id: 'e5', from: 'a2', to: 'e' },
  ],
});
const stage = (code: string, p: Partial<PreviewStage> = {}): PreviewStage => ({ code, name: { 'zh-TW': code }, status: 'enter', approvers: [{ userId: 'u1', displayName: '王小明' }], usesFallback: false, ...p });
const result = (p: Partial<ApprovalPreview>): ApprovalPreview => ({ outcome: 'flow', flowName: {}, flowKey: 'mine', stages: [], picks: [], path: { nodeIds: [], edgeIds: [] }, ...p });

describe('trialOverlay', () => {
  it('path nodes and edges are highlighted, others dimmed', () => {
    const o = trialOverlay(def(), result({ stages: [stage('a2')], path: { nodeIds: ['s', 'c', 'a2', 'e'], edgeIds: ['e1', 'e3', 'e5'] } }), 'mine')!;
    expect(Object.fromEntries(Object.entries(o.nodes).map(([k, v]) => [k, v.state]))).toEqual({ s: 'path', c: 'path', a1: 'off', a2: 'path', e: 'path' });
    expect(o.edges).toEqual({ e1: 'path', e2: 'off', e3: 'path', e4: 'off', e5: 'path' });
  });

  it('skipped nodes are faded: on the path but not entered', () => {
    const o = trialOverlay(def(), result({ stages: [stage('a2', { status: 'skip', approvers: [] })], path: { nodeIds: ['s', 'c', 'a2', 'e'], edgeIds: ['e1', 'e3', 'e5'] } }), 'mine')!;
    expect(o.nodes.a2.state).toBe('skipped');
    expect(o.nodes.a2.approvers).toEqual([]);
  });

  it('each approval node carries its resolved approvers, fallback, pick and problem', () => {
    const o = trialOverlay(def(), result({
      stages: [stage('a1', { usesFallback: true }), stage('a2', { approvers: [], problem: 'noApprover' })],
      path: { nodeIds: ['s', 'c', 'a1', 'a2', 'e'], edgeIds: [] },
    }), 'mine')!;
    expect(o.nodes.a1).toMatchObject({ approvers: ['王小明'], fallback: true, problem: null, pick: false });
    expect(o.nodes.a2).toMatchObject({ approvers: [], problem: 'noApprover' });
    const t = trialOverlay(def(), result({ stages: [stage('a1', { status: 'pick', approvers: [] })], path: { nodeIds: ['a1'], edgeIds: [] } }), 'mine')!;
    expect(t.nodes.a1.pick).toBe(true);
    const many = trialOverlay(def(), result({ stages: [stage('a1', { problem: 'tooManyApprovers' })], path: { nodeIds: ['a1'], edgeIds: [] } }), 'mine')!;
    expect(many.nodes.a1.problem).toBe('tooManyApprovers');
  });

  it('parallel branches are marked per branch with the any-group', () => {
    const d: GraphDefinition = {
      shape: 'graph', stages: [],
      nodes: [{ id: 's', kind: 'start' }, { id: 'f', kind: 'fork' }, appr('a1'), appr('a2'), { id: 'j', kind: 'join', mode: 'any' }, { id: 'e', kind: 'end' }],
      edges: [{ id: 'e1', from: 's', to: 'f' }, { id: 'e2', from: 'f', to: 'a1' }, { id: 'e3', from: 'f', to: 'a2' }, { id: 'e4', from: 'a1', to: 'j' }, { id: 'e5', from: 'a2', to: 'j' }, { id: 'e6', from: 'j', to: 'e' }],
    };
    const o = trialOverlay(d, result({ stages: [stage('a1', { anyGroup: 'j' }), stage('a2', { anyGroup: 'j' })], path: { nodeIds: ['s', 'f', 'a1', 'a2', 'j', 'e'], edgeIds: ['e1', 'e2', 'e3', 'e4', 'e5', 'e6'] } }), 'mine')!;
    expect(o.nodes.a1.anyGroup).toBe('j');
    expect(o.nodes.a2.anyGroup).toBe('j');
    expect(o.nodes.s.anyGroup).toBeNull();
  });

  it('conditional reveals stop at the condition node: it reads as undecided and everything after is dimmed', () => {
    const o = trialOverlay(def(), result({
      stages: [{ code: 'c', name: {}, status: 'conditional', approvers: [], usesFallback: false }], path: { nodeIds: ['s', 'c'], edgeIds: ['e1'] },
    }), 'mine')!;
    expect(o.nodes.c.state).toBe('undecided');
    expect(o.nodes.s.state).toBe('path');
    expect(['a1', 'a2', 'e'].map((id) => o.nodes[id].state)).toEqual(['off', 'off', 'off']);
    expect(o.edges.e1).toBe('path');
    expect(o.edges.e2).toBe('off');
  });

  it('no overlay when nothing maps onto this graph: other flow matched, blocked, direct, no path, no result', () => {
    const p = { nodeIds: ['s'], edgeIds: [] };
    expect(trialOverlay(def(), null, 'mine')).toBeNull();
    expect(trialOverlay(def(), result({ flowKey: 'other', path: p }), 'mine')).toBeNull();
    expect(trialOverlay(def(), result({ outcome: 'blocked', path: p }), 'mine')).toBeNull();
    expect(trialOverlay(def(), result({ outcome: 'direct', path: p }), 'mine')).toBeNull();
    expect(trialOverlay(def(), result({ path: null }), 'mine')).toBeNull();
    expect(trialOverlay(def(), result({ path: undefined }), 'mine')).toBeNull();
  });
});
