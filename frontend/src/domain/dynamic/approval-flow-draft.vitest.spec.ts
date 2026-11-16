import { describe, expect, it } from 'vitest';
import type { ApprovalFlow } from './approval-flow-types';
import {
  buildFlowInput, definitionChanged, draftFromFlow, emptyFlowDraft, insertStage, moveStage, newStage, nextPriority, removeStage, stageErrors,
} from './approval-flow-draft';

const flow = (key: string, to: number, priority: number): ApprovalFlow => ({
  key, name: { 'zh-TW': key }, toOptionId: to, priority, active: true, moduleKey: 'm', version: 1, used: false, pendingCount: 0,
  definition: {
    shape: 'sequence', when: { all: [{ field: 7, op: 'gte', value: 100 }] } as never, rejectTo: 11, fallbackApprovers: { source: 'users', userIds: ['u1'] },
    stages: [
      { code: 'a', name: { 'zh-TW': 'A' }, when: null, approvers: { source: 'group', groupIds: [1, 2] }, mode: 'all', editableFields: [7], dueHours: 24, onEmpty: 'skip' },
      { code: 'b', name: { 'zh-TW': 'B' }, when: null, approvers: { source: 'manager', level: 2 }, mode: 'any', editableFields: [], dueHours: null, onEmpty: 'fallback' },
    ],
  },
});

describe('approval flow draft', () => {
  it('newStage picks the smallest unused stage-N code', () => {
    expect(newStage([]).code).toBe('stage-1');
    expect(newStage(['stage-1', 'stage-3']).code).toBe('stage-2');
    expect(newStage(['stage-1', 'stage-2']).code).toBe('stage-3');
  });

  it('insert keeps existing codes and never renames', () => {
    const d = { ...emptyFlowDraft(12, 1), stages: [{ ...newStage([]), code: 'stage-1' }, { ...newStage([]), code: 'stage-3' }] };
    const next = insertStage(d, 1);
    expect(next.stages.map((s) => s.code)).toEqual(['stage-1', 'stage-2', 'stage-3']);
    expect(insertStage(d, 0).stages[0]!.code).toBe('stage-2');
    expect(insertStage(d, 2).stages[2]!.code).toBe('stage-2');
  });

  it('buildFlowInput prunes empty conditions to null', () => {
    const d = { ...emptyFlowDraft(12, 1), key: 'k', name: { 'zh-TW': 'n' }, when: { all: [{ field: 0, op: 'eq' }] } as never };
    d.stages = [{ ...d.stages[0]!, when: { all: [] } as never }];
    const input = buildFlowInput(d)!;
    expect(input.definition.when).toBeNull();
    expect(input.definition.stages[0]!.when).toBeNull();
    expect(input.definition.shape).toBe('sequence');
  });

  it('buildFlowInput is null without a target state', () => {
    expect(buildFlowInput(emptyFlowDraft(null, 1))).toBeNull();
  });

  it('a saved single-person stage with mode all round-trips unchanged', () => {
    const f = flow('k', 12, 1);
    f.definition.stages[0] = { ...f.definition.stages[0]!, approvers: { source: 'users', userIds: ['u1'] }, mode: 'all' };
    expect(buildFlowInput(draftFromFlow(f))!.definition).toEqual(f.definition);
  });

  it('insert avoids reserved saved codes as well as the draft codes', () => {
    const d = { ...emptyFlowDraft(12, 1), stages: [{ ...newStage([]), code: 'stage-1' }] };
    expect(insertStage(d, 1, ['stage-1', 'stage-2']).stages[1]!.code).toBe('stage-3');
  });

  it('draftFromFlow then buildFlowInput equals the original definition', () => {
    const f = flow('k', 12, 3);
    expect(buildFlowInput(draftFromFlow(f))).toEqual({
      key: 'k', name: f.name, toOptionId: 12, priority: 3, active: true, definition: f.definition,
    });
  });

  it('nextPriority is max+1 in the target, 1 when empty, ignores the flow itself', () => {
    const flows = [flow('a', 12, 1), flow('b', 12, 4), flow('c', 13, 9)];
    expect(nextPriority(flows, 12)).toBe(5);
    expect(nextPriority(flows, 14)).toBe(1);
    expect(nextPriority(flows, 12, 'b')).toBe(2);
    expect(nextPriority([flow('a', 12, 1)], 12, 'a')).toBe(1);
  });

  it('removeStage keeps the last stage; move swaps neighbours', () => {
    const d = draftFromFlow(flow('k', 12, 1));
    expect(removeStage(d, 0).stages.map((s) => s.code)).toEqual(['b']);
    expect(removeStage(removeStage(d, 0), 0).stages).toHaveLength(1);
    expect(moveStage(d, 0, 1).stages.map((s) => s.code)).toEqual(['b', 'a']);
    expect(moveStage(d, 0, -1)).toBe(d);
  });

  it('stageErrors maps definition.stages[i] keys by field', () => {
    const errors = { 'definition.stages[1].approvers': ['無人'], 'definition.stages[1].dueHours': ['太大'], 'definition.stages[10].code': ['x'], 'definition.stages[0].name': ['y'], 'key': ['z'] };
    expect(stageErrors(errors, 1)).toEqual({ approvers: '無人', dueHours: '太大' });
    expect(stageErrors(errors, 10)).toEqual({ code: 'x' });
    expect(stageErrors(undefined, 0)).toEqual({});
  });

  it('definitionChanged: untouched draft, name, priority and active are not definition changes; stage content is', () => {
    const f = flow('a', 12, 1);
    const d = draftFromFlow(f);
    expect(definitionChanged(d, f)).toBe(false);
    expect(definitionChanged({ ...d, name: { 'zh-TW': 'x' }, priority: 9, active: false }, f)).toBe(false);
    expect(definitionChanged({ ...d, stages: [{ ...d.stages[0]!, mode: 'any' }, d.stages[1]!] }, f)).toBe(true);
    expect(definitionChanged({ ...d, rejectTo: 12 }, f)).toBe(true);
  });
});
