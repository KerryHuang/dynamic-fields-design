import { describe, expect, it } from 'vitest';
import {
  buildBoardConfig, changeGroupBy, DEFAULT_BOARD_CONFIG, formFromConfig, moveInList, saveAsConfig, scopeOptions, setFilterAssignees,
} from './task-board-config';

describe('task-board-config', () => {
  it('round-trips the default board config', () => {
    expect(buildBoardConfig(formFromConfig(DEFAULT_BOARD_CONFIG, true))).toEqual(DEFAULT_BOARD_CONFIG);
  });

  it('builds filters only for what was set and drops empty groups', () => {
    const form = formFromConfig({ version: 1, groupBy: 'status', scope: 'visible' }, true);
    expect(buildBoardConfig(form)).toEqual({ version: 1, groupBy: 'status', scope: 'visible', sort: { key: 'dueDate', dir: 'asc' } });
    form.keyword = '  報價 ';
    setFilterAssignees(form, ['sales01']);
    form.statuses = ['todo'];
    expect(buildBoardConfig(form).filters).toEqual({ statuses: ['todo'], assigneeUserIds: ['sales01'], keyword: '報價' });
  });

  it('includeUnassigned is sent only when it differs from what the assignee list implies', () => {
    const form = formFromConfig({ version: 1, groupBy: 'status', scope: 'visible' }, true);
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
    const form = formFromConfig({ version: 1, groupBy: 'status', scope: 'assigned', columns: { order: ['done'], hidden: ['cancelled'] } }, true);
    expect(form.order).toEqual(['done', 'todo', 'inProgress', 'cancelled']);
    expect(form.hidden).toEqual(['cancelled']);
    expect(buildBoardConfig(form).columns).toEqual({ order: ['done', 'todo', 'inProgress', 'cancelled'], hidden: ['cancelled'] });
  });

  it('changing the group-by resets column order and hidden (keys differ per group-by)', () => {
    const form = formFromConfig({ version: 1, groupBy: 'status', scope: 'assigned', columns: { hidden: ['cancelled'] } }, true);
    changeGroupBy(form, 'dueBucket');
    expect(buildBoardConfig(form).columns).toBeUndefined();
  });

  it('assignee boards carry explicit assignees and auto-append, and hide only the unassigned column', () => {
    const form = formFromConfig({ version: 1, groupBy: 'assignee', scope: 'subordinates', columns: { assignees: ['a', 'b'], autoAppendAssignees: false } }, true);
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

  it('the subordinates scope exists only with the capability', () => {
    expect(scopeOptions(true)).toEqual(['assigned', 'involved', 'subordinates', 'visible']);
    expect(scopeOptions(false)).toEqual(['assigned', 'involved', 'visible']);
  });

  it('a form opened without the capability turns a subordinates scope into assigned (D7)', () => {
    expect(formFromConfig({ version: 1, groupBy: 'status', scope: 'subordinates' }, false).scope).toBe('assigned');
    expect(formFromConfig({ version: 1, groupBy: 'status', scope: 'subordinates' }, true).scope).toBe('subordinates');
  });

  it('saveAsConfig swaps an unavailable subordinates scope for assigned and leaves other configs alone', () => {
    const base = { version: 1, groupBy: 'assignee' as const, scope: 'subordinates' as const, columns: { assignees: ['a'] } };
    expect(saveAsConfig(base, false)).toEqual({ ...base, scope: 'assigned' });
    expect(saveAsConfig(base, true)).toBe(base);
  });
});
