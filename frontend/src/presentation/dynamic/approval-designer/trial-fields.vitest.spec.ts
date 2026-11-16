import { describe, expect, it } from 'vitest';
import type { ApprovalDefinition, ApprovalFlowInput, GraphDefinition } from 'src/domain/dynamic/approval-flow-types';
import type { ConditionNode } from 'src/domain/dynamic/types';
import type { ApproverSource } from 'src/domain/dynamic/approval-flow-types';
import { trialFieldIds } from './trial-fields';

const def = (when: ConditionNode | null, stageWhen: ConditionNode | null, approvers: ApproverSource = { source: 'manager', level: 1 }): ApprovalDefinition => ({
  shape: 'sequence', when, stages: [{ code: 's', name: {}, when: stageWhen, approvers, mode: 'any', editableFields: [], onEmpty: 'fallback' }],
});
const flow = (key: string, to: number, d: ApprovalDefinition | null) => ({ key, toOptionId: to, definition: d });
const draft = (to: number, d: ApprovalDefinition): ApprovalFlowInput => ({ key: 'draft', name: {}, toOptionId: to, priority: 1, active: true, definition: d });

describe('trialFieldIds', () => {
  it('collects fields from flow and stage conditions of all flows on the target plus userField sources', () => {
    const flows = [flow('a', 12, def({ all: [{ field: 7, op: 'gte', value: 1 }] }, { any: [{ field: 8, op: 'eq', value: 'x' }, { field: 9, op: 'gt', value: { fieldRef: 10 } }] }))];
    const d = draft(12, def(null, null, { source: 'userField', fieldId: 20 }));
    expect(trialFieldIds(flows, d).sort((x, y) => x - y)).toEqual([7, 8, 9, 10, 20]);
  });
  it('ignores flows on other targets', () => {
    const flows = [flow('a', 13, def({ all: [{ field: 7, op: 'gte', value: 1 }] }, null))];
    expect(trialFieldIds(flows, draft(12, def({ all: [{ field: 5, op: 'eq', value: 1 }] }, null)))).toEqual([5]);
  });
  it('uses the draft instead of the saved flow of the same key', () => {
    const flows = [flow('draft', 12, def({ all: [{ field: 7, op: 'gte', value: 1 }] }, null))];
    expect(trialFieldIds(flows, draft(12, def({ all: [{ field: 8, op: 'gte', value: 1 }] }, null)))).toEqual([8]);
  });
});

const graph = (extra: Partial<GraphDefinition> = {}): GraphDefinition => ({
  shape: 'graph', stages: [],
  nodes: [
    { id: 'start', kind: 'start' },
    { id: 'a1', kind: 'approval', when: { all: [{ field: 31, op: 'gte', value: 1 }] }, approvers: { source: 'userField', fieldId: 32 }, mode: 'any' },
    { id: 'c1', kind: 'condition' },
    { id: 'end', kind: 'end' },
  ],
  edges: [
    { id: 'e1', from: 'start', to: 'a1' }, { id: 'e2', from: 'a1', to: 'c1' },
    { id: 'e3', from: 'c1', to: 'end', order: 1, when: { all: [{ field: 33, op: 'gte', value: { fieldRef: 34 } }] } },
    { id: 'e4', from: 'c1', to: 'end', default: true },
  ],
  ...extra,
});

describe('trialFieldIds with graph definitions', () => {
  it('trialFieldIds reads graph node skip conditions, userField sources and condition edges', () => {
    const d = draft(12, graph({ when: { all: [{ field: 30, op: 'eq', value: 1 }] } }));
    expect(trialFieldIds([], d).sort((x, y) => x - y)).toEqual([30, 31, 32, 33, 34]);
  });
  it('reads the graph of other flows on the same target and skips draft-only flows (no definition)', () => {
    const flows = [flow('g', 12, graph()), flow('only-draft', 12, null), flow('other', 13, graph())];
    expect(trialFieldIds(flows, draft(12, def(null, null))).sort((x, y) => x - y)).toEqual([31, 32, 33, 34]);
  });
});
