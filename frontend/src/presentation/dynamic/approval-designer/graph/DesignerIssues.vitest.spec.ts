import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { ApprovalFlow, GraphDefinition } from 'src/domain/dynamic/approval-flow-types';
import { mapGraphIssues } from 'src/domain/dynamic/approval-graph-errors';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import DesignerIssues from './DesignerIssues.vue';
import { useGraphDesigner } from './use-graph-designer';

const def: GraphDefinition = {
  shape: 'graph', stages: [],
  nodes: [{ id: 's', kind: 'start' }, { id: 'a', kind: 'approval', name: { 'zh-TW': '財務' }, approvers: { source: 'manager', level: 1 }, mode: 'any', editableFields: [], onEmpty: 'fallback' }, { id: 'e', kind: 'end' }],
  edges: [{ id: 'e1', from: 's', to: 'a' }, { id: 'e2', from: 'a', to: 'e' }],
};
const flow = { key: 'po', name: { 'zh-TW': '採購' }, toOptionId: 12, priority: 1, active: true, used: false, pendingCount: 0, moduleKey: 'doc', version: 1, versionId: 3, definition: def, graph: def } as ApprovalFlow;

function mountIssues(errors: Record<string, string[]>, warnings: { key: string; message: string; code?: string }[] = []) {
  const designer = useGraphDesigner(() => true);
  designer.load(flow);
  designer.issues.value = mapGraphIssues(errors, warnings as never, def);
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(DesignerIssues, { props: { designer }, global: { plugins: [[quasar.Quasar, { components: quasar }], i18n] } });
}

describe('DesignerIssues', () => {
  it('lists errors before warnings, with the node name, and marks the severity', () => {
    const w = mountIssues({ 'definition.nodes[1].approvers': ['請選簽核人'], 'definition.edges[0]': ['這條線不通'] }, [{ key: 'definition.nodes[1]', message: '可能略過', code: 'W1' }]);
    const rows = w.findAll('[data-testid^="de-issue-"]');
    expect(rows.map((r) => r.attributes('data-severity'))).toEqual(['error', 'error', 'warning']);
    expect(rows[0].text()).toContain('請選簽核人');
    expect(rows[2].text()).toContain('警告');
    expect(rows[2].text()).toContain('可能略過');
  });

  it('clicking an issue emits the node, edge or flow as the target', async () => {
    const w = mountIssues({ 'definition.nodes[1].approvers': ['x'], 'definition.edges[1]': ['y'], toOptionId: ['z'] });
    const picks = w.findAll('[data-testid^="de-issue-"]');
    for (const p of picks) await p.trigger('click');
    expect(w.emitted('pick')!.map((e) => e[0])).toEqual([{ kind: 'flow' }, { kind: 'node', id: 'a' }, { kind: 'edge', id: 'e2' }]);
  });

  it('says there is nothing to fix when empty, and closes', async () => {
    const w = mountIssues({});
    expect(w.get('[data-testid="de-issues-empty"]').text()).toBe('沒有錯誤或警告');
    await w.get('[data-testid="de-issues-close"]').trigger('click');
    expect(w.emitted('close')).toHaveLength(1);
  });
});
