import { mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { ApprovalActionView } from 'src/domain/dynamic/approval-types';
import enUS from 'src/i18n/en-US.json';
import zhTW from 'src/i18n/zh-TW.json';
import ApprovalActionLog from './ApprovalActionLog.vue';

const action = (p: Partial<ApprovalActionView>): ApprovalActionView => ({
  action: 'transfer', actorUserId: 'a', actorName: '進銷存主管', onBehalfOfUserId: null, targetUserIds: [], comment: null, data: null, createdTime: '2026-11-14T01:00:00', ...p,
});
const render = (actions: ApprovalActionView[], locale = 'zh-TW') => mount(ApprovalActionLog, {
  props: { actions, schema: null },
  global: { plugins: [createI18n({ legacy: false, locale, messages: { 'zh-TW': zhTW, 'en-US': enUS } })] },
});

describe('ApprovalActionLog', () => {
  it('shows the target names of a transfer and a reassign, next to the comment', () => {
    const w = render([
      action({ action: 'transfer', targetUserIds: ['mgr'], targetNames: ['業務主管'], comment: '出差中' }),
      action({ action: 'reassign', actorName: '系統管理員', targetUserIds: ['fin1'], targetNames: ['財務一'] }),
    ]);
    const logs = w.findAll('[data-testid="approval-log"] > li');
    expect(logs[0]!.find('[data-testid="log-targets"]').text()).toBe('→ 業務主管');
    expect(logs[0]!.find('[data-testid="log-comment"]').text()).toBe('出差中');
    expect(logs[1]!.find('[data-testid="log-targets"]').text()).toBe('→ 財務一');
    expect(logs[1]!.find('[data-testid="log-comment"]').exists()).toBe(false);
  });

  it('shows the add-sign users of an add-sign entry', () => {
    const w = render([action({ action: 'add-sign', targetUserIds: ['s1', 's2'], targetNames: ['業務一', '業務二'], comment: '請確認數量', data: { mode: 'any' } })]);
    const log = w.find('[data-testid="approval-log"] > li');
    expect(log.find('[data-testid="log-targets"]').text()).toBe('→ 業務一、業務二');
    expect(log.find('[data-testid="log-comment"]').text()).toBe('請確認數量');
  });

  it('falls back to the user ids without names, and shows no target for other actions', () => {
    const w = render([action({ action: 'transfer', targetUserIds: ['mgr'] }), action({ action: 'approve', targetUserIds: ['x'], comment: '好' })]);
    expect(w.findAll('[data-testid="log-targets"]').map((e) => e.text())).toEqual(['→ mgr']);
  });

  it('joins several targets with the locale list separator', () => {
    const w = render([action({ action: 'transfer', targetUserIds: ['a', 'b'], targetNames: ['Ann', 'Bob'] })], 'en-US');
    expect(w.find('[data-testid="log-targets"]').text()).toBe('→ Ann, Bob');
  });

  it('ActionLog_BranchCancelled_UsesLocaleNotComment: words the cancellation from data.name in the reader language, never the stored zh-TW comment', () => {
    const a = action({ action: 'branch-cancelled', actorName: 'System', comment: '分支「財務」已完成，其他分支自動取消', data: { name: { 'zh-TW': '財務', 'en-US': 'Finance' } } });
    const en = render([a], 'en-US');
    expect(en.find('[data-testid="approval-log"] > li').text()).toContain('Other branches cancelled');
    expect(en.find('[data-testid="log-branch-cancelled-note"]').text()).toBe('Branch "Finance" finished; the other branches were cancelled');
    expect(en.find('[data-testid="log-comment"]').exists()).toBe(false);
    expect(en.text()).not.toContain('分支「財務」');
    const zh = render([a]);
    expect(zh.find('[data-testid="log-branch-cancelled-note"]').text()).toBe('分支「財務」已完成，其他分支自動取消');
    expect(zh.find('[data-testid="log-comment"]').exists()).toBe(false);
  });

  it('ActionLog_BranchCancelled_NoName_Unnamed: without data.name it says the unnamed branch finished', () => {
    const zh = render([action({ action: 'branch-cancelled', comment: '分支「（未命名分支）」已完成，其他分支自動取消', data: { forkId: 'f' } })]);
    expect(zh.find('[data-testid="log-branch-cancelled-note"]').text()).toBe('分支「未命名分支」已完成，其他分支自動取消');
    const en = render([action({ action: 'branch-cancelled', data: null })], 'en-US');
    expect(en.find('[data-testid="log-branch-cancelled-note"]').text()).toBe('Branch "Unnamed branch" finished; the other branches were cancelled');
  });

  it('ActionLog_BranchCancelled_NameFallsBackToZhTw: a name without the reader language uses the zh-TW text', () => {
    const w = render([action({ action: 'branch-cancelled', data: { name: { 'zh-TW': '財務' } } })], 'en-US');
    expect(w.find('[data-testid="log-branch-cancelled-note"]').text()).toBe('Branch "財務" finished; the other branches were cancelled');
  });
});
