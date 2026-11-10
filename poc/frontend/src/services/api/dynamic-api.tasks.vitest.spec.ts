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
});
