import { createPinia, setActivePinia } from 'pinia';
import { beforeEach, describe, expect, it } from 'vitest';
import { dynamicApi } from './dynamic-api';
import { http } from './http';

interface Seen { method?: string; url?: string; body?: unknown; headers?: Record<string, unknown> }

function stub(data: unknown, status = 200): Seen {
  const seen: Seen = {};
  http.defaults.adapter = async (config) => {
    seen.method = config.method;
    seen.url = http.getUri(config);
    seen.body = config.data;
    seen.headers = config.headers as unknown as Record<string, unknown>;
    if (status >= 400) return Promise.reject({ config, response: { status, data, headers: {}, config } });
    return { data, status, statusText: 'OK', headers: {}, config };
  };
  return seen;
}

describe('dynamicApi tasks', () => {
  beforeEach(() => setActivePinia(createPinia()));

  it('fills the properties the server omits when null', async () => {
    stub({ id: 1, title: 'T', status: 'todo', assigneeActive: false, overdue: false, fromRule: false, canEdit: true });
    const t = await dynamicApi.getTask(1);
    expect(t).toMatchObject({
      assigneeUserId: null, assigneeName: null, dueDate: null, record: null, description: null,
      createdBy: null, createdByName: null, completedTime: null, ruleKey: null,
    });
  });

  it('normalizes the record ref and the list page', async () => {
    stub({ items: [{ id: 2, title: 'A', status: 'todo', record: { accessible: false, deleted: false } }], totalCount: 1, currentPage: 1, pagesSize: 20 });
    const page = await dynamicApi.getMyTasks('mentioned', 'overdue', 3);
    expect(page.items[0].record).toEqual({ accessible: false, moduleKey: null, recordId: null, title: null, deleted: false });
    expect(page.items[0].assigneeUserId).toBeNull();
  });

  it('requests the list with tab, filter and paging', async () => {
    const seen = stub({ items: [], totalCount: 0, currentPage: 1, pagesSize: 20 });
    await dynamicApi.getMyTasks('assigned', 'open', 1);
    expect(seen.url).toBe('/api/v3/dynamic/tasks?tab=assigned&filter=open&currentPage=1&pagesSize=20');
  });

  it('normalizes comments: a withdrawn one has no body, missing lists become empty', async () => {
    stub([{ id: 3, kind: 'comment', withdrawn: true, createdTime: 'x', canWithdraw: false }]);
    const [c] = await dynamicApi.getTaskComments(1);
    expect(c).toMatchObject({ body: null, mentions: [], attachments: [], systemEvent: null, systemData: null, authorUserId: null });
  });

  it('normalizes record tasks', async () => {
    stub({ items: [{ id: 1, title: 'x', status: 'done' }], canAdd: true });
    const r = await dynamicApi.getRecordTasks(9);
    expect(r.manualRules).toEqual([]);
    expect(r.items[0].dueDate).toBeNull();
  });

  it('keeps explicit null in a PATCH body so the server clears the field', async () => {
    const seen = stub({ id: 1, title: 'x', status: 'todo' });
    await dynamicApi.updateTask(1, { dueDate: null });
    expect(seen.method).toBe('patch');
    expect(JSON.parse(String(seen.body))).toEqual({ dueDate: null });
  });

  it('sends the mention keyword as `keyword`', async () => {
    const seen = stub([]);
    await dynamicApi.getMentionCandidates('業');
    expect(decodeURIComponent(seen.url!)).toBe('/api/v3/dynamic/users/mention-candidates?keyword=業');
  });

  it('posts a comment with files as multipart: one body plus every file', async () => {
    const seen = stub({ id: 1, kind: 'comment', withdrawn: false });
    const a = new File(['a'], 'a.txt');
    const b = new File(['b'], 'b.txt');
    await dynamicApi.addTaskCommentWithFiles(5, '看一下', [a, b]);
    expect(seen.url).toBe('/api/v3/dynamic/tasks/5/comments');
    const form = seen.body as FormData;
    expect(form.get('body')).toBe('看一下');
    expect(form.getAll('files')).toHaveLength(2);
  });

  it('a 413 without a JSON body still becomes an ApiError with a status', async () => {
    stub('', 413);
    await expect(dynamicApi.addTaskCommentWithFiles(5, 'x', [new File(['a'], 'a.txt')])).rejects.toMatchObject({ status: 413 });
  });

  it('a 413 with an HTML string body does not throw while parsing', async () => {
    stub('<html>Request Entity Too Large</html>', 413);
    await expect(dynamicApi.addTaskCommentWithFiles(5, 'x', [])).rejects.toMatchObject({ status: 413, message: undefined });
  });

  it('updateTask normalizes a visible:false response without task fields', async () => {
    stub({ id: 5, visible: false, undoToken: 'tok' });
    const r = await dynamicApi.updateTask(5, { assigneeUserId: 'sales01' });
    expect(r).toEqual({ id: 5, visible: false, undoToken: 'tok' });
  });

  it('updateTask still normalizes a normal reply, including the manager fields', async () => {
    stub({ id: 5, title: 'T', status: 'todo', canEdit: false, canReassign: true, supervised: true, version: '20261105010000000' });
    const r = await dynamicApi.updateTask(5, { assigneeUserId: 'sales01' });
    expect(r).toMatchObject({ id: 5, canReassign: true, supervised: true, version: '20261105010000000', assigneeUserId: null });
  });

  it('getTaskCapabilities and undoReassign call the manager endpoints', async () => {
    const seen = stub({ subordinates: true });
    expect(await dynamicApi.getTaskCapabilities()).toEqual({ subordinates: true });
    expect(seen.url).toBe('/api/v3/dynamic/tasks/capabilities');
    const undo = stub({ id: 5, title: 'T', status: 'todo' });
    await dynamicApi.undoReassign(5, 'tok');
    expect(undo).toMatchObject({ method: 'post', url: '/api/v3/dynamic/tasks/5/undo-reassign' });
    expect(JSON.parse(undo.body as string)).toEqual({ undoToken: 'tok' });
  });

  it('board columns posts the source, an empty one means the default board, and null properties are filled in', async () => {
    const seen = stub({
      today: '2026-11-17', weekEnd: '2026-11-22', laterDefault: '2026-11-29', config: { version: 1, groupBy: 'dueBucket', scope: 'visible' }, columns: [
        { key: 'todo', kind: 'status', total: 1, cards: [{ id: 3, title: 'A', status: 'todo', record: { accessible: false, deleted: false } }], hasMore: false, truncated: false, droppable: true },
        { key: 'user:sales01', kind: 'user', userId: 'sales01', total: 0, inactive: true, droppable: false },
      ],
    });
    const board = await dynamicApi.getBoardColumns({});
    expect(seen).toMatchObject({ method: 'post', url: '/api/v3/dynamic/task-board/columns' });
    expect(JSON.parse(seen.body as string)).toEqual({ viewId: null, config: null });
    expect(board.scopeAvailable).toBe(true);
    expect(board.omittedAssignees).toBe(0);
    // 伺服器採用的設定原樣帶回，分欄依據以它為準
    expect(board.config).toEqual({ version: 1, groupBy: 'dueBucket', scope: 'visible' });
    expect(board.columns[0].cards[0]).toMatchObject({ assigneeUserId: null, dueDate: null, commentCount: 0, record: { accessible: false, moduleKey: null } });
    expect(board.columns[1]).toMatchObject({ name: null, inactive: true, cards: [], droppable: false });
    await dynamicApi.getBoardColumns({ viewId: 7 });
    expect(JSON.parse(seen.body as string)).toEqual({ viewId: 7, config: null });
  });

  it('board column page posts the offset to the encoded column key; move posts the target and the version', async () => {
    const page = stub({ key: 'user:a b', total: 80, cards: [{ id: 9, title: 'X', status: 'done' }], hasMore: true });
    const r = await dynamicApi.getBoardColumnPage('user:a b', { viewId: 2 }, 50);
    expect(page.url).toBe('/api/v3/dynamic/task-board/columns/user%3Aa%20b/page');
    expect(JSON.parse(page.body as string)).toEqual({ viewId: 2, config: null, offset: 50 });
    expect(r).toMatchObject({ total: 80, hasMore: true, truncated: false });
    const move = stub({ visible: false, undoToken: 'tok' });
    const m = await dynamicApi.moveTask(5, { groupBy: 'assignee', toColumn: 'user:sales02', expectedVersion: 'v1' });
    expect(move).toMatchObject({ method: 'post', url: '/api/v3/dynamic/tasks/5/move' });
    expect(m).toEqual({ visible: false, card: null, undoToken: 'tok' });
  });

  it('task views: list, detail, save, copy, shares and share targets use the task-views endpoints', async () => {
    const list = stub({ mine: [{ id: 1, name: 'A', isOwner: true }] });
    expect((await dynamicApi.getTaskViews()).shared).toEqual([]);
    expect(list.url).toBe('/api/v3/dynamic/task-views?kind=board');
    const detail = stub({ id: 1, name: 'A', config: { version: 1, groupBy: 'status', scope: 'assigned' }, rowVersion: 'r1' });
    expect((await dynamicApi.getTaskView(1)).shares).toEqual([]);
    expect(detail.url).toBe('/api/v3/dynamic/task-views/1');
    await dynamicApi.updateTaskView(1, { name: 'B', rowVersion: 'r1' });
    expect(detail).toMatchObject({ method: 'put', url: '/api/v3/dynamic/task-views/1' });
    await dynamicApi.copyTaskView(1);
    expect(detail).toMatchObject({ method: 'post', url: '/api/v3/dynamic/task-views/1/copy' });
    await dynamicApi.setTaskViewShares(1, [{ kind: 'department', id: '3' }]);
    expect(detail).toMatchObject({ method: 'put', url: '/api/v3/dynamic/task-views/1/shares' });
    expect(JSON.parse(detail.body as string)).toEqual([{ kind: 'department', id: '3' }]);
    stub({});
    expect(await dynamicApi.getTaskViewShareTargets()).toEqual({ users: [], departments: [], groups: [] });
  });
});
