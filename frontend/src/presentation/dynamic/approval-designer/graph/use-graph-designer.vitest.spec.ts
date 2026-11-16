import { describe, expect, it } from 'vitest';
import type { ApprovalFlow, GraphDefinition } from 'src/domain/dynamic/approval-flow-types';
import { setNode } from 'src/domain/dynamic/approval-graph-edit';
import { useGraphDesigner } from './use-graph-designer';

const def = (): GraphDefinition => ({
  shape: 'graph', stages: [],
  nodes: [{ id: 's', kind: 'start' }, { id: 'a', kind: 'approval', name: { 'zh-TW': '財務' }, approvers: { source: 'manager', level: 1 }, mode: 'any' }, { id: 'e', kind: 'end' }],
  edges: [{ id: 'e1', from: 's', to: 'a' }, { id: 'e2', from: 'a', to: 'e' }],
});
const flow = (p: Partial<ApprovalFlow> = {}): ApprovalFlow => ({
  key: 'po', name: { 'zh-TW': '採購' }, toOptionId: 3, priority: 2, active: true, used: false, pendingCount: 0, moduleKey: 'm', version: 1, versionId: 11, definition: def(), graph: def(), ...p,
});
const mk = (canEdit = true) => { const d = useGraphDesigner(() => canEdit); return d; };
const rename = (n: string) => (s: ReturnType<typeof useGraphDesigner>['state']['value']) => setNode(s!, 'a', { name: { 'zh-TW': n } });

describe('useGraphDesigner', () => {
  it('loads the draft when there is one, else the published graph', () => {
    const draft = def();
    draft.nodes[1].name = { 'zh-TW': '草稿財務' };
    const d = mk();
    d.load(flow({ draft: { definition: draft }, draftRev: 'r1' }));
    expect(d.state.value!.definition.nodes[1].name).toEqual({ 'zh-TW': '草稿財務' });
    expect(d.server.value).toMatchObject({ hasDraft: true, draftRev: 'r1', versionId: 11, published: true, isNew: false });
    d.load(flow({ draftRev: 'after-publish' }));
    expect(d.state.value!.definition.nodes[1].name).toEqual({ 'zh-TW': '財務' });
    expect(d.server.value.hasDraft).toBe(false);
  });

  it('a flow with no graph at all does not load', () => {
    expect(mk().load(flow({ definition: undefined, graph: undefined, version: undefined }))).toBe(false);
  });

  it('an edit marks unsaved, undo returns to clean', () => {
    const d = mk();
    d.load(flow());
    expect(d.dirty.value).toBe(false);
    d.edit(rename('X'));
    expect(d.dirty.value).toBe(true);
    d.undo();
    expect(d.dirty.value).toBe(false);
    expect(d.redoable.value).toBe(true);
  });

  it('typing in one field is one undo step; another action cuts it', () => {
    const d = mk();
    d.load(flow());
    d.edit(rename('X'), 'a.name');
    d.edit(rename('XY'), 'a.name');
    d.edit(rename('XYZ'), 'a.name');
    d.undo();
    expect(d.state.value!.definition.nodes[1].name).toEqual({ 'zh-TW': '財務' });
    expect(d.undoable.value).toBe(false);
    d.edit(rename('P'), 'a.name');
    d.move('a', { x: 1, y: 1 });
    d.edit(rename('Q'), 'a.name');
    d.undo(); d.undo(); d.undo();
    expect(d.state.value!.definition.nodes[1].name).toEqual({ 'zh-TW': '財務' });
  });

  it('dragging a node is one step that only changes the position', () => {
    const d = mk();
    d.load(flow());
    const before = d.state.value!.definition;
    d.move('a', { x: 40, y: 8 });
    expect(d.state.value!.positions.a).toEqual({ x: 40, y: 8 });
    expect(d.state.value!.definition).toBe(before);
    expect(d.dirty.value).toBe(true);
  });

  it('a read-only designer ignores every write', () => {
    const d = mk(false);
    d.load(flow());
    d.edit(rename('X'));
    d.editMeta({ name: { 'zh-TW': 'Y' } });
    d.move('a', { x: 1, y: 1 });
    expect(d.dirty.value).toBe(false);
  });

  it('flow-level fields are part of the history', () => {
    const d = mk();
    d.load(flow());
    d.editMeta({ toOptionId: 5 });
    expect(d.dirty.value).toBe(true);
    d.undo();
    expect(d.meta.value!.toOptionId).toBe(3);
  });

  it('save keeps history but clears unsaved; clearHistory empties it (U60)', () => {
    const d = mk();
    d.load(flow());
    d.edit(rename('X'));
    d.markSaved();
    expect(d.dirty.value).toBe(false);
    expect(d.undoable.value).toBe(true);
    d.undo();
    expect(d.dirty.value).toBe(true);
    d.redo();
    d.clearHistory();
    expect(d.undoable.value).toBe(false);
    expect(d.redoable.value).toBe(false);
  });

  it('a new flow starts from the starter graph with the target preset and is not unsaved', () => {
    const d = mk();
    d.load(null, 7);
    expect(d.server.value.isNew).toBe(true);
    expect(d.meta.value).toMatchObject({ key: '', toOptionId: 7 });
    expect(d.state.value!.definition.nodes.map((n) => n.kind)).toEqual(['start', 'approval', 'end']);
    expect(d.dirty.value).toBe(false);
  });

  it('reserves the codes of the published version so new nodes do not reuse them', () => {
    const d = mk();
    d.load(flow());
    expect(d.server.value.reserved).toEqual(['s', 'a', 'e']);
    d.load(flow({ definition: undefined, graph: undefined, version: undefined, versionId: undefined, draft: { definition: def() } }));
    expect(d.server.value.reserved).toEqual([]);
  });
});
