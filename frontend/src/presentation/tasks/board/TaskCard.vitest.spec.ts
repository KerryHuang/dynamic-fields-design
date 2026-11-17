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

  it('labels an unassigned or inactive assignee, and opens on click and on Enter', async () => {
    expect(mountCard('status', { assigneeUserId: null, assigneeName: null }).get('[data-testid="board-card-assignee"]').text()).toContain('未指派');
    expect(mountCard('status', { assigneeActive: false }).get('[data-testid="board-card-assignee"]').text()).toContain('業務一（已停用）');
    const w = mountCard('status');
    await w.get('[data-testid="board-card-1"]').trigger('click');
    await w.get('[data-testid="board-card-1"]').trigger('keydown.enter');
    expect(w.emitted('open')).toEqual([[1], [1]]);
    expect(w.get('[data-testid="board-card-1"]').attributes('tabindex')).toBe('0');
  });
});
