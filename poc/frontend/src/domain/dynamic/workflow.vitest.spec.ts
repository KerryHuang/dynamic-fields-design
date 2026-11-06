import { describe, expect, it } from 'vitest';
import type { SchemaWorkflow } from './types';
import {
  buildWorkflow, canAdvance, draftFromWorkflow, emptyWorkflowDraft, intersectAllowed, isReadOnlyState, selectableStates,
  setStateIncluded, setStateReadOnly, withStatusField,
} from './workflow';

const wf: SchemaWorkflow = {
  statusFieldKey: 'f_5', initialState: 'opt_11',
  states: [{ value: 'opt_11', readOnly: false }, { value: 'opt_12', readOnly: true }, { value: 'opt_13', readOnly: true }],
  transitions: [{ from: 'opt_11', to: 'opt_12' }, { from: 'opt_12', to: 'opt_13' }],
};

describe('selectableStates', () => {
  it('has no restriction without a workflow', () => {
    expect(selectableStates(undefined, 'edit', 'opt_11')).toBeUndefined();
  });
  it('offers only the initial state on create', () => {
    expect([...selectableStates(wf, 'create', null)!]).toEqual(['opt_11']);
  });
  it('offers the current state plus the reachable ones on edit', () => {
    expect([...selectableStates(wf, 'edit', 'opt_11')!].sort()).toEqual(['opt_11', 'opt_12']);
    expect([...selectableStates(wf, 'edit', 'opt_12')!].sort()).toEqual(['opt_12', 'opt_13']);
  });
  it('offers only the current state when the user may not advance it', () => {
    expect([...selectableStates(wf, 'edit', 'opt_13')!]).toEqual(['opt_13']);
  });
  it('has no restriction on edit when the stored state is empty', () => {
    expect(selectableStates(wf, 'edit', null)).toBeUndefined();
  });
});

describe('state helpers', () => {
  it('reads readOnly and advance per state', () => {
    expect(isReadOnlyState(wf, 'opt_11')).toBe(false);
    expect(isReadOnlyState(wf, 'opt_12')).toBe(true);
    expect(isReadOnlyState(undefined, 'opt_12')).toBe(false);
    expect(canAdvance(wf, 'opt_12')).toBe(true);
    expect(canAdvance(wf, 'opt_13')).toBe(false);
    expect(canAdvance(undefined, 'opt_11')).toBe(false);
  });
  it('intersects allowed sets with undefined meaning everything', () => {
    expect(intersectAllowed(undefined, undefined)).toBeUndefined();
    expect([...intersectAllowed(new Set(['a', 'b']), undefined)!]).toEqual(['a', 'b']);
    expect([...intersectAllowed(undefined, new Set(['a']))!]).toEqual(['a']);
    expect([...intersectAllowed(new Set(['a', 'b']), new Set(['b', 'c']))!]).toEqual(['b']);
  });
});

describe('workflow draft', () => {
  const draft = draftFromWorkflow({
    statusFieldId: 5, initialOptionId: 11,
    states: [{ optionId: 11, readOnly: false }, { optionId: 12, readOnly: true }],
    transitions: [{ from: 11, to: 12, groupIds: [3] }],
  });

  it('starts empty without a saved workflow', () => {
    expect(draftFromWorkflow(null)).toEqual(emptyWorkflowDraft());
  });
  it('turning a state off drops its transitions and the initial mark', () => {
    const next = setStateIncluded(draft, 11, false);
    expect(next.states.map((s) => s.optionId)).toEqual([12]);
    expect(next.initialOptionId).toBeNull();
    expect(next.transitions).toEqual([]);
    expect(draft.transitions).toHaveLength(1); // the input is not mutated
  });
  it('turning a state on adds it once, not read-only', () => {
    const next = setStateIncluded(draft, 13, true);
    expect(next.states.at(-1)).toEqual({ optionId: 13, readOnly: false });
    expect(setStateIncluded(next, 13, true)).toBe(next);
  });
  it('sets readOnly on one state', () => {
    expect(setStateReadOnly(draft, 11, true).states.map((s) => s.readOnly)).toEqual([true, true]);
  });
  it('resets everything when the status field changes', () => {
    expect(withStatusField(draft, 5)).toBe(draft);
    expect(withStatusField(draft, 6)).toEqual({ ...emptyWorkflowDraft(), statusFieldId: 6 });
  });
  it('builds the body and drops half-filled transition rows', () => {
    const withHalf = { ...draft, transitions: [...draft.transitions, { from: 12, to: null, groupIds: [] }] };
    expect(buildWorkflow(withHalf)).toEqual({
      statusFieldId: 5, initialOptionId: 11,
      states: [{ optionId: 11, readOnly: false }, { optionId: 12, readOnly: true }],
      transitions: [{ from: 11, to: 12, groupIds: [3] }],
    });
  });
  it('builds nothing without a status field or an initial state', () => {
    expect(buildWorkflow(emptyWorkflowDraft())).toBeUndefined();
    expect(buildWorkflow({ ...draft, initialOptionId: null })).toBeUndefined();
  });
});
