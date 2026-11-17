import { describe, expect, it } from 'vitest';
import {
  buildBoardConfig, changeGroupBy, formFromConfig, MAX_RANGE_N, moveInList, rangeNInvalid, saveAsConfig, scopeOptions, setFilterAssignees,
} from './task-board-config';

describe('task-board-config', () => {
  it('round-trips a board config the server sent', () => {
    const served = { version: 1, groupBy: 'status' as const, scope: 'assigned' as const, columns: { hidden: ['cancelled'] }, sort: { key: 'dueDate' as const, dir: 'asc' as const } };
    expect(buildBoardConfig(formFromConfig(served))).toEqual(served);
  });

  it('builds filters only for what was set and drops empty groups', () => {
    const form = formFromConfig({ version: 1, groupBy: 'status', scope: 'visible' });
    expect(buildBoardConfig(form)).toEqual({ version: 1, groupBy: 'status', scope: 'visible', sort: { key: 'dueDate', dir: 'asc' } });
    form.keyword = '  報價 ';
    setFilterAssignees(form, ['sales01']);
    form.statuses = ['todo'];
    expect(buildBoardConfig(form).filters).toEqual({ statuses: ['todo'], assigneeUserIds: ['sales01'], keyword: '報價' });
  });

  it('includeUnassigned is sent only when it differs from what the assignee list implies', () => {
    const form = formFromConfig({ version: 1, groupBy: 'status', scope: 'visible' });
    expect(form.includeUnassigned).toBe(true);
    expect(buildBoardConfig(form).filters).toBeUndefined();
    form.includeUnassigned = false;
    expect(buildBoardConfig(form).filters).toEqual({ includeUnassigned: false });
    form.assigneeUserIds = ['sales01'];
    expect(buildBoardConfig(form).filters).toEqual({ assigneeUserIds: ['sales01'] });
    form.includeUnassigned = true;
    expect(buildBoardConfig(form).filters).toEqual({ assigneeUserIds: ['sales01'], includeUnassigned: true });
  });

  it('keeps the fixed column order and hidden keys, appending keys the config did not list', () => {
    const form = formFromConfig({ version: 1, groupBy: 'status', scope: 'assigned', columns: { order: ['done'], hidden: ['cancelled'] } });
    expect(form.order).toEqual(['done', 'todo', 'inProgress', 'cancelled']);
    expect(form.hidden).toEqual(['cancelled']);
    expect(buildBoardConfig(form).columns).toEqual({ order: ['done', 'todo', 'inProgress', 'cancelled'], hidden: ['cancelled'] });
  });

  it('changing the group-by resets column order and hidden (keys differ per group-by)', () => {
    const form = formFromConfig({ version: 1, groupBy: 'status', scope: 'assigned', columns: { hidden: ['cancelled'] } });
    changeGroupBy(form, 'dueBucket');
    expect(buildBoardConfig(form).columns).toBeUndefined();
  });

  it('assignee boards carry explicit assignees and auto-append, and hide only the unassigned column', () => {
    const form = formFromConfig({ version: 1, groupBy: 'assignee', scope: 'subordinates', columns: { assignees: ['a', 'b'], autoAppendAssignees: false } });
    expect(form.assignees).toEqual(['a', 'b']);
    expect(form.autoAppend).toBe(false);
    form.hidden = ['unassigned'];
    expect(buildBoardConfig(form).columns).toEqual({ hidden: ['unassigned'], assignees: ['a', 'b'], autoAppendAssignees: false });
  });

  it('moveInList swaps neighbours and ignores the ends', () => {
    expect(moveInList(['a', 'b', 'c'], 'b', -1)).toEqual(['b', 'a', 'c']);
    expect(moveInList(['a', 'b', 'c'], 'b', 1)).toEqual(['a', 'c', 'b']);
    expect(moveInList(['a', 'b', 'c'], 'a', -1)).toEqual(['a', 'b', 'c']);
    expect(moveInList(['a', 'b', 'c'], 'c', 1)).toEqual(['a', 'b', 'c']);
  });

  it('the subordinates scope is offered only with the capability, or when the config already has it (D7)', () => {
    expect(scopeOptions(true)).toEqual(['assigned', 'involved', 'subordinates', 'visible']);
    expect(scopeOptions(false)).toEqual(['assigned', 'involved', 'visible']);
    expect(scopeOptions(false, 'subordinates')).toEqual(['assigned', 'involved', 'subordinates', 'visible']);
    expect(scopeOptions(false, 'visible')).toEqual(['assigned', 'involved', 'visible']);
  });

  it('formFromConfig keeps the scope as it is, even a subordinates scope the user can no longer pick (D7)', () => {
    expect(formFromConfig({ version: 1, groupBy: 'status', scope: 'subordinates' }).scope).toBe('subordinates');
  });

  it('rangeNInvalid flags a missing, non-integer or out-of-range N, only for ranges that take one', () => {
    const form = formFromConfig({ version: 1, groupBy: 'status', scope: 'assigned', filters: { dueRange: { kind: 'lastDays', n: 7 } } });
    expect(rangeNInvalid(form)).toBe(false);
    for (const n of [0, -1, 1.5, null, MAX_RANGE_N + 1]) { form.dueRange = { kind: 'lastDays', n }; expect(rangeNInvalid(form), String(n)).toBe(true); }
    form.dueRange = { kind: 'lastDays', n: MAX_RANGE_N };
    expect(rangeNInvalid(form)).toBe(false);
    form.dueRange = { kind: 'thisWeek', n: null };
    expect(rangeNInvalid(form)).toBe(false);
    form.dueRange = null;
    expect(rangeNInvalid(form)).toBe(false);
  });

  it('saveAsConfig swaps an unavailable subordinates scope for assigned and leaves other configs alone', () => {
    const base = { version: 1, groupBy: 'assignee' as const, scope: 'subordinates' as const, columns: { assignees: ['a'] } };
    expect(saveAsConfig(base, false)).toEqual({ ...base, scope: 'assigned' });
    expect(saveAsConfig(base, true)).toBe(base);
  });
});
