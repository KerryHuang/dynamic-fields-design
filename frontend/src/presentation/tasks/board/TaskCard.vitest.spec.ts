import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { BoardGroupBy } from 'src/domain/dynamic/task-board-types';
import zhTW from 'src/i18n/zh-TW.json';
import { normalizeBoardCard } from 'src/services/api/task-normalize';
import { cardWire } from './board-fixtures';
import TaskCard from './TaskCard.vue';

function mountCard(groupBy: BoardGroupBy, wire: Record<string, unknown> = {}) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(TaskCard, {
    props: { card: normalizeBoardCard(cardWire(wire) as never), groupBy },
    global: { plugins: [[quasar.Quasar, { components: quasar }], i18n] },
  });
}

describe('TaskCard', () => {
  it('TaskCard hides the assignee on an assignee board and the status on a status board', () => {
    const byStatus = mountCard('status');
    expect(byStatus.find('[data-testid="board-card-status"]').exists()).toBe(false);
    expect(byStatus.get('[data-testid="board-card-assignee"]').text()).toContain('業務一');
    const byAssignee = mountCard('assignee');
    expect(byAssignee.find('[data-testid="board-card-assignee"]').exists()).toBe(false);
    expect(byAssignee.get('[data-testid="board-card-status"]').text()).toContain('待辦');
    const byDue = mountCard('dueBucket');
    expect(byDue.find('[data-testid="board-card-status"]').exists()).toBe(true);
    expect(byDue.find('[data-testid="board-card-assignee"]').exists()).toBe(true);
  });

  it('shows the no-access record text and nothing for a personal task', () => {
    const noAccess = mountCard('status', { record: { accessible: false, deleted: false } });
    expect(noAccess.get('[data-testid="board-card-record"]').text()).toBe('相關記錄（無權限查看）');
    expect(noAccess.text()).not.toContain('crm_customer');
    expect(mountCard('status', { record: null }).find('[data-testid="board-card-record"]').exists()).toBe(false);
    expect(mountCard('status').get('[data-testid="board-card-record"]').text()).toBe('甲公司');
  });

  it('marks overdue and today with semantic colors; closed tasks are struck through', () => {
    expect(mountCard('status', { overdue: true }).get('[data-testid="board-card-due"]').classes()).toContain('text-negative');
    expect(mountCard('status', { dueToday: true }).get('[data-testid="board-card-due"]').classes()).toContain('text-warning');
    expect(mountCard('status').get('[data-testid="board-card-due"]').classes()).not.toContain('text-negative');
    expect(mountCard('status', { status: 'done' }).get('[data-testid="board-card-title"]').classes()).toContain('text-strike');
  });

  it('shows the rule icon, the frozen icon and the comment count only when they apply', () => {
    const plain = mountCard('status');
    for (const id of ['rule', 'frozen', 'comments']) expect(plain.find(`[data-testid="board-card-${id}"]`).exists()).toBe(false);
    const full = mountCard('status', { fromRule: true, frozen: true, commentCount: 3 });
    expect(full.find('[data-testid="board-card-rule"]').exists()).toBe(true);
    expect(full.find('[data-testid="board-card-frozen"]').exists()).toBe(true);
    expect(full.get('[data-testid="board-card-comments"]').text()).toContain('3');
  });

  it('the rule, frozen and comment markers have a name that assistive technology can read (not hidden)', () => {
    const full = mountCard('status', { fromRule: true, frozen: true, commentCount: 3 });
    const names = ['rule', 'frozen', 'comments'].map((id) => full.get(`[data-testid="board-card-${id}"]`));
    expect(names.map((n) => n.attributes('aria-label'))).toEqual(['由規則產生', '相關記錄已鎖定', '3 則留言']);
    for (const n of names) {
      expect(n.attributes('role')).toBe('img');
      // the named node and every ancestor up to the card must not be aria-hidden
      for (let el: Element | null = n.element; el && el !== full.element; el = el.parentElement) expect(el.getAttribute('aria-hidden')).not.toBe('true');
    }
    // the icon inside stays decorative
    expect(full.get('[data-testid="board-card-rule"] .q-icon').attributes('aria-hidden')).toBe('true');
  });

  it('the card is a plain container and the title is the only button, so a later move button is not nested in a button', () => {
    const w = mountCard('status');
    const root = w.get('[data-testid="board-card-1"]');
    expect(root.element.tagName).toBe('DIV');
    expect(root.attributes('role')).toBeUndefined();
    expect(root.attributes('tabindex')).toBeUndefined();
    const open = w.get('[data-testid="board-card-open-1"]');
    expect(open.element.tagName).toBe('BUTTON');
    expect(open.attributes('type')).toBe('button');
    expect(open.text()).toBe('回覆恆川機械樣品問題');
    expect(w.findAll('button')).toHaveLength(1);
  });

  it('a long assignee name is cut off inside the card', () => {
    const el = mountCard('status', { assigneeName: 'a-very-long-account-name' }).get('[data-testid="board-card-assignee"]');
    expect(el.get('.ellipsis').text()).toBe('a-very-long-account-name');
  });

  it('labels an unassigned or inactive assignee, and opens from the title button', async () => {
    expect(mountCard('status', { assigneeUserId: null, assigneeName: null }).get('[data-testid="board-card-assignee"]').text()).toContain('未指派');
    expect(mountCard('status', { assigneeActive: false }).get('[data-testid="board-card-assignee"]').text()).toContain('業務一（已停用）');
    const w = mountCard('status');
    await w.get('[data-testid="board-card-open-1"]').trigger('click');
    expect(w.emitted('open')).toEqual([[1]]);
  });
});
