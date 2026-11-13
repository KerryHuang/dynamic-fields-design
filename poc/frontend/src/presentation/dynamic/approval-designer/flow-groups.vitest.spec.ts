import { describe, expect, it } from 'vitest';
import type { ApprovalFlow } from 'src/domain/dynamic/approval-flow-types';
import type { Workflow } from 'src/domain/dynamic/types';
import { groupFlows, moveKeys, moveKeyTo } from './flow-groups';

const flow = (key: string, toOptionId: number, priority: number) => ({ key, toOptionId, priority }) as ApprovalFlow;
const wf: Workflow = {
  statusFieldId: 1, initialOptionId: 11,
  states: [{ optionId: 11, readOnly: false }, { optionId: 12, readOnly: false }, { optionId: 13, readOnly: true }],
  transitions: [
    { from: 11, to: 12, groupIds: [], approval: { required: true, noMatch: 'block' } },
    { from: 12, to: 13, groupIds: [] },
  ],
};
const label = (id: number) => `S${id}`;

describe('groupFlows', () => {
  it('groups by target in workflow state order, flows by priority', () => {
    const g = groupFlows([flow('b', 13, 2), flow('a', 12, 2), flow('c', 12, 1)], wf, label);
    expect(g.map((x) => x.toOptionId)).toEqual([12, 13]);
    expect(g[0]!.flows.map((f) => f.key)).toEqual(['c', 'a']);
  });
  it('entries list from → to with approval transitions only', () => {
    const g = groupFlows([flow('a', 12, 1), flow('b', 13, 1)], wf, label);
    expect(g[0]!.entries).toEqual(['S11 → S12']);
    expect(g[0]!.hasApprovalEntry).toBe(true);
    expect(g[1]!.hasApprovalEntry).toBe(false);
  });
  it('a state with an approval entry but no flow still gets a group', () => {
    const g = groupFlows([], wf, label);
    expect(g.map((x) => x.toOptionId)).toEqual([12]);
    expect(g[0]!.flows).toEqual([]);
  });
  it('without a workflow, flows still group by target', () => {
    expect(groupFlows([flow('a', 5, 1)], undefined, label).map((x) => x.toOptionId)).toEqual([5]);
  });
});

describe('moveKeys', () => {
  it('swaps within the given list only and returns null at the ends', () => {
    expect(moveKeys(['a', 'b', 'c'], 'b', 1)).toEqual(['a', 'c', 'b']);
    expect(moveKeys(['a', 'b', 'c'], 'a', -1)).toBeNull();
    expect(moveKeys(['a'], 'a', 1)).toBeNull();
  });
  it('moveKeyTo puts the dragged key where the target was', () => {
    expect(moveKeyTo(['a', 'b', 'c'], 'a', 'c')).toEqual(['b', 'c', 'a']);
    expect(moveKeyTo(['a', 'b', 'c'], 'c', 'a')).toEqual(['c', 'a', 'b']);
    expect(moveKeyTo(['a', 'b'], 'a', 'a')).toBeNull();
  });
});
