import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'src/i18n/zh-TW.json';
import DesignerNode from './DesignerNode.vue';
import type { DesignerNodeData } from './designer-graph';
import type { NodeTrial } from './designer-trial';

const data = (p: Partial<DesignerNodeData> = {}): DesignerNodeData => ({
  kind: 'approval', label: '財務', summary: '直屬主管 · 第 1 層 · 或簽', icon: 'person', width: 168, height: 84, skip: false, errors: 0, warnings: 0, selected: false, ariaLabel: '財務 · 簽核', ...p,
});
const mountNode = (d: DesignerNodeData) => mount(DesignerNode, {
  props: { data: d }, global: { plugins: [[quasar.Quasar, { components: quasar }], createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } })], stubs: { Handle: true } },
});

const trial = (p: Partial<NodeTrial> = {}): NodeTrial => ({ state: 'path', approvers: [], pick: false, fallback: false, problem: null, anyGroup: null, ...p });

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

  describe('trial overlay', () => {
    it('an approval node on the path shows its resolved approvers instead of the source summary', () => {
      const w = mountNode(data({ trial: trial({ approvers: ['王小明', '李小華'] }) }));
      expect(w.get('[data-testid="de-summary"]').text()).toBe('王小明、李小華');
      expect(w.get('[data-testid="de-node"]').classes()).toContain('de-trial-path');
    });

    it('a problem is a badge with the short text and the full explanation as title', () => {
      const w = mountNode(data({ trial: trial({ problem: 'noApprover' }) }));
      const b = w.get('[data-testid="de-trial-problem"]');
      expect(b.text()).toBe('無簽核人');
      expect(b.attributes('title')).toContain('找不到簽核人');
      expect(mountNode(data({ trial: trial({ problem: 'tooManyApprovers' }) })).get('[data-testid="de-trial-problem"]').text()).toBe('人數過多');
    });

    it('a parallel branch is marked any-branch, a fallback is marked, a picked approver reads as the initiator picks', () => {
      const w = mountNode(data({ trial: trial({ approvers: ['王小明'], anyGroup: 'j1', fallback: true }) }));
      expect(w.get('[data-testid="de-trial-any"]').text()).toBe('任一分支');
      expect(w.get('[data-testid="de-trial-fallback"]').text()).toBe('備援');
      expect(mountNode(data({ trial: trial({ pick: true }) })).get('[data-testid="de-summary"]').text()).toBe('由發起人送出時選擇');
    });

    it('skipped and undecided nodes say so; skipped is faded, undecided stays highlighted', () => {
      const s = mountNode(data({ trial: trial({ state: 'skipped' }) }));
      expect(s.get('[data-testid="de-summary"]').text()).toBe('不需簽核');
      expect(s.get('[data-testid="de-node"]').classes()).toContain('de-trial-skipped');
      const u = mountNode(data({ kind: 'condition', summary: '2 條出線', trial: trial({ state: 'undecided' }) }));
      expect(u.get('[data-testid="de-summary"]').text()).toBe('依條件決定');
      expect(u.get('[data-testid="de-node"]').classes()).toContain('de-trial-undecided');
    });

    it('a node off the path keeps its summary, is dimmed and shows no trial badges', () => {
      const w = mountNode(data({ trial: trial({ state: 'off', problem: 'noApprover', anyGroup: 'j1' }) }));
      expect(w.get('[data-testid="de-summary"]').text()).toContain('或簽');
      expect(w.get('[data-testid="de-node"]').classes()).toContain('de-trial-off');
      expect(w.find('[data-testid="de-trial-problem"]').exists()).toBe(false);
      expect(w.find('[data-testid="de-trial-any"]').exists()).toBe(false);
    });

    it('without a trial there is no trial class and the summary is the source summary', () => {
      const w = mountNode(data());
      expect(w.get('[data-testid="de-node"]').classes().some((c) => c.startsWith('de-trial'))).toBe(false);
      expect(w.get('[data-testid="de-summary"]').text()).toContain('或簽');
    });
  });
});
