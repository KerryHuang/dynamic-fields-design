import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import { defineComponent } from 'vue';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { useAddTaskComment, useDeleteTask, useUndoReassign, useUpdateTask } from './tasks-queries';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: { updateTask: vi.fn(), undoReassign: vi.fn(), deleteTask: vi.fn(), addTaskComment: vi.fn(), addTaskCommentWithFiles: vi.fn() },
}));
const api = vi.mocked(dynamicApi);

function setup() {
  const qc = new QueryClient();
  const spy = vi.spyOn(qc, 'invalidateQueries');
  let composed!: { update: ReturnType<typeof useUpdateTask>; del: ReturnType<typeof useDeleteTask>; comment: ReturnType<typeof useAddTaskComment>; undo: ReturnType<typeof useUndoReassign> };
  mount(defineComponent({
    setup() { composed = { update: useUpdateTask(), del: useDeleteTask(), comment: useAddTaskComment(), undo: useUndoReassign() }; return () => null; },
  }), { global: { plugins: [[VueQueryPlugin, { queryClient: qc }]] } });
  const keys = () => spy.mock.calls.map((c) => JSON.stringify((c[0] as { queryKey: unknown[] }).queryKey));
  return { composed, keys };
}

describe('task mutations invalidate everything that shows a task', () => {
  it('update: lists, counters, record block, the task and its timeline', async () => {
    api.updateTask.mockResolvedValue({} as never);
    const { composed, keys } = setup();
    await composed.update.mutateAsync({ id: 7, patch: { status: 'done' } });
    await flushPromises();
    expect(keys()).toEqual(expect.arrayContaining(['["tasks"]', '["task-summary"]', '["record-tasks"]', '["task",7]', '["task-comments",7]']));
  });

  it('comment with files uses the multipart call, without files the JSON one', async () => {
    api.addTaskComment.mockResolvedValue({} as never);
    api.addTaskCommentWithFiles.mockResolvedValue({} as never);
    const { composed } = setup();
    await composed.comment.mutateAsync({ id: 7, body: 'a', files: [] });
    expect(api.addTaskComment).toHaveBeenCalledWith(7, 'a');
    const f = new File(['x'], 'x.txt');
    await composed.comment.mutateAsync({ id: 7, body: 'b', files: [f] });
    expect(api.addTaskCommentWithFiles).toHaveBeenCalledWith(7, 'b', [f]);
  });

  it('delete refreshes lists and counters', async () => {
    api.deleteTask.mockResolvedValue(undefined as never);
    const { composed, keys } = setup();
    await composed.del.mutateAsync(7);
    await flushPromises();
    expect(keys()).toEqual(expect.arrayContaining(['["tasks"]', '["task-summary"]', '["record-tasks"]']));
  });

  it('undo reassign sends the token and refreshes lists, counters and the task', async () => {
    api.undoReassign.mockResolvedValue({} as never);
    const { composed, keys } = setup();
    await composed.undo.mutateAsync({ id: 7, undoToken: 'tok' });
    await flushPromises();
    expect(api.undoReassign).toHaveBeenCalledWith(7, 'tok');
    expect(keys()).toEqual(expect.arrayContaining(['["tasks"]', '["task-summary"]', '["task",7]', '["task-comments",7]']));
  });
});
