import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'src/i18n/zh-TW.json';
import DesignerNode from './DesignerNode.vue';
import type { DesignerNodeData } from './designer-graph';

const data = (p: Partial<DesignerNodeData> = {}): DesignerNodeData => ({
  kind: 'approval', label: '財務', summary: '直屬主管 · 第 1 層 · 或簽', icon: 'person', width: 168, height: 84, skip: false, errors: 0, warnings: 0, selected: false, ariaLabel: '財務 · 簽核', ...p,
});
const mountNode = (d: DesignerNodeData) => mount(DesignerNode, {
  props: { data: d }, global: { plugins: [[quasar.Quasar, { components: quasar }], createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } })], stubs: { Handle: true } },
});

describe('DesignerNode', () => {
  it('shows kind icon, name, approver summary and the skip-condition badge', () => {
    const w = mountNode(data({ skip: true }));
    expect(w.get('[data-testid="de-name"]').text()).toBe('財務');
    expect(w.get('[data-testid="de-summary"]').text()).toContain('或簽');
    expect(w.find('.q-icon').text()).toBe('person');
    expect(w.find('[data-testid="de-skip"]').exists()).toBe(true);
  });

  it('a button with an accessible name; Enter and Space open it', async () => {
    const w = mountNode(data());
    const root = w.get('[data-testid="de-node"]');
    expect(root.attributes('role')).toBe('button');
    expect(root.attributes('tabindex')).toBe('0');
    expect(root.attributes('aria-label')).toBe('財務 · 簽核');
    await root.trigger('keydown.enter');
    await root.trigger('keydown.space');
    expect(w.emitted('open')).toHaveLength(2);
  });

  it('error and warning badges carry counts and labels; errors win the frame', () => {
    const w = mountNode(data({ errors: 2, warnings: 1 }));
    expect(w.get('[data-testid="de-errors"]').attributes('aria-label')).toBe('2 項錯誤');
    expect(w.get('[data-testid="de-warnings"]').attributes('aria-label')).toBe('1 項警告');
    expect(w.get('[data-testid="de-node"]').classes()).toContain('de-has-error');
    expect(w.get('[data-testid="de-node"]').classes()).not.toContain('de-has-warn');
  });

  it('selected node is marked', () => {
    expect(mountNode(data({ selected: true })).get('[data-testid="de-node"]').classes()).toContain('de-selected');
  });
});
