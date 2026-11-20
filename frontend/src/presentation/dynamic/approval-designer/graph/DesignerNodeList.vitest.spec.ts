import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { ApprovalFlow, GraphDefinition } from 'src/domain/dynamic/approval-flow-types';
import { mapGraphIssues } from 'src/domain/dynamic/approval-graph-errors';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import DesignerNodeList from './DesignerNodeList.vue';
import type { Selection } from './designer-graph';
import { useGraphDesigner } from './use-graph-designer';

// 節點在定義裡的順序和從開始走的順序不同，用來證明清單是依流程順序
const def: GraphDefinition = {
  shape: 'graph', stages: [],
  nodes: [
    { id: 'e', kind: 'end' }, { id: 'b', kind: 'approval', name: { 'zh-TW': '總經理' }, approvers: { source: 'manager', level: 2 }, mode: 'any', editableFields: [], onEmpty: 'fallback' },
    { id: 'a', kind: 'approval', name: { 'zh-TW': '財務' }, approvers: { source: 'manager', level: 1 }, mode: 'any', editableFields: [], onEmpty: 'fallback' }, { id: 's', kind: 'start' },
  ],
  edges: [{ id: 'e1', from: 's', to: 'a' }, { id: 'e2', from: 'a', to: 'b' }, { id: 'e3', from: 'b', to: 'e' }],
};
const flow = { key: 'po', name: { 'zh-TW': '採購' }, toOptionId: 12, priority: 1, active: true, used: false, pendingCount: 0, moduleKey: 'doc', version: 1, versionId: 3, definition: def, graph: def } as ApprovalFlow;

function mountList(opts: { canEdit?: boolean; selection?: Selection } = {}) {
  const designer = useGraphDesigner(() => opts.canEdit ?? true);
  designer.load(flow);
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return { designer, w: mount(DesignerNodeList, { props: { designer, selection: opts.selection ?? { kind: 'flow' } }, attachTo: document.body, global: { plugins: [[quasar.Quasar, { components: quasar }], i18n] } }) };
}

describe('DesignerNodeList', () => {
  it('follows the order from the start node', () => {
    const { w } = mountList();
    expect(w.findAll('li button').map((b) => b.text())).toEqual(['1開始開始', '2財務簽核', '3總經理簽核', '4結束結束']);
  });

  it('clicking an item picks that node', async () => {
    const { w } = mountList();
    await w.get('[data-testid="de-nodelist-b"]').trigger('click');
    expect(w.emitted('pick')).toEqual([['b']]);
  });

  it('is keyboard operable: every item is a native button in the tab order, and the current one is marked', () => {
    const { w } = mountList({ selection: { kind: 'node', id: 'a' } });
    const buttons = w.findAll('li button');
    expect(buttons.every((b) => b.element.tagName === 'BUTTON' && b.attributes('tabindex') === undefined && b.attributes('type') === 'button')).toBe(true);
    expect(w.get('[data-testid="de-nodelist-a"]').attributes('aria-current')).toBe('true');
    expect(w.get('[data-testid="de-nodelist-b"]').attributes('aria-current')).toBeUndefined();
    expect(w.get('[role="region"]').attributes('aria-label')).toBe('節點清單');
  });

  it('is available to a read-only user and shows the error count of a node', () => {
    const { w, designer } = mountList({ canEdit: false });
    designer.issues.value = mapGraphIssues({ 'definition.nodes[1].approvers': ['x', 'y'] }, [], def);
    return w.vm.$nextTick().then(() => {
      expect(w.findAll('li button').length).toBe(4);
      expect(w.get('[data-testid="de-nodelist-b"]').text()).toContain('2');
    });
  });
});
