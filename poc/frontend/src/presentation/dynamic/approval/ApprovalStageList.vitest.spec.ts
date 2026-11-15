import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { ApprovalStageView } from 'src/domain/dynamic/approval-types';
import enUS from 'src/i18n/en-US.json';
import zhTW from 'src/i18n/zh-TW.json';
import ApprovalStageList from './ApprovalStageList.vue';

const stage = (p: Partial<ApprovalStageView> = {}): ApprovalStageView => ({
  id: 1, seq: 1, stageCode: 'a', name: { 'zh-TW': '法務', 'en-US': 'Legal' }, mode: 'any', status: 'closed', skipReason: null, dueTime: null, tasks: [], editableFieldKeys: [], ...p,
});
const render = (stages: ApprovalStageView[], locale = 'zh-TW') => mount(ApprovalStageList, {
  props: { stages },
  global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], createI18n({ legacy: false, locale, messages: { 'zh-TW': zhTW, 'en-US': enUS } })] },
});
const textOf = (w: ReturnType<typeof render>, id: number) => w.find(`[data-testid="stage-${id}"]`).text();

describe('ApprovalStageList', () => {
  it('StageList_BranchCancelled_ShowsNoNeedToHandle: a branch cancelled by an any-join says no action is needed', () => {
    const w = render([stage({ closeReason: 'branchCancelled', nodeId: 'law', branchKey: 'fa' })]);
    expect(textOf(w, 1)).toContain('已結束');
    expect(textOf(w, 1)).toContain('其他分支已完成，不需處理');
    expect(w.find('[data-testid="stage-close-reason"]').exists()).toBe(true);
  });

  it('shows the reason in the reader language', () => {
    const w = render([stage({ closeReason: 'branchCancelled' })], 'en-US');
    expect(textOf(w, 1)).toContain('Another branch has finished; no action needed');
  });

  it('StageList_InstanceEnded_NoExtraText: a stage closed because the whole request ended keeps the plain status', () => {
    const w = render([stage({ closeReason: 'instanceEnded' })]);
    expect(textOf(w, 1)).toContain('已結束');
    expect(w.find('[data-testid="stage-close-reason"]').exists()).toBe(false);
    expect(textOf(w, 1)).not.toContain('不需處理');
  });

  it('a sequence stage (no closeReason) is unchanged', () => {
    const w = render([stage({ id: 2, status: 'approved' })]);
    expect(w.find('[data-testid="stage-close-reason"]').exists()).toBe(false);
  });
});
