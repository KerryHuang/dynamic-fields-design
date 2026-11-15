import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { PreviewStage } from 'src/domain/dynamic/approval-types';
import zhTW from 'src/i18n/zh-TW.json';
import PreviewStages from './PreviewStages.vue';

const mountIt = (stages: PreviewStage[], designer = false) => mount(PreviewStages, {
  props: { stages, designer },
  global: { plugins: [[quasar.Quasar, { components: quasar }], createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } })] },
});
const stage = (extra: Partial<PreviewStage>): PreviewStage => ({ code: 'a', name: { 'zh-TW': 'A' }, status: 'enter', approvers: [{ userId: 'u', displayName: '王' }], usesFallback: false, ...extra });

describe('PreviewStages', () => {
  it('problem text renders; record preview without problem is unchanged', () => {
    const w = mountIt([stage({ problem: 'noApprover' }), stage({ code: 'b' })]);
    expect(w.find('[data-testid="preview-problem-a"]').text()).toBe('找不到簽核人，送出或推進到這一關時會失敗');
    expect(w.find('[data-testid="preview-problem-b"]').exists()).toBe(false);
    expect(w.find('[data-testid="preview-stage-b"]').text()).toContain('王');
  });
  it('a pick stage reads as the viewer choice by default and as the initiator choice in the designer', () => {
    const pick = stage({ status: 'pick', approvers: [] });
    expect(mountIt([pick]).text()).toContain('由你選擇');
    expect(mountIt([pick], true).text()).toContain('由發起人送出時選擇');
  });
  it('any-join note shows once above the first item of each group, not for other items', () => {
    const w = mountIt([
      stage({ code: 'mgr' }),
      stage({ code: 'legal', anyGroup: 'merge' }), stage({ code: 'fin', anyGroup: 'merge' }),
      stage({ code: 'z' }),
      stage({ code: 'p', anyGroup: 'other' }),
    ]);
    const notes = w.findAll('[data-testid^="preview-any-group-"]');
    expect(notes.map((n) => n.attributes('data-testid'))).toEqual(['preview-any-group-merge', 'preview-any-group-other']);
    expect(notes[0].text()).toBe('以下分支任一條完成即可');
    // the note sits on the first item of the group
    expect(w.find('[data-testid="preview-stage-legal"]').find('[data-testid="preview-any-group-merge"]').exists()).toBe(true);
    expect(w.find('[data-testid="preview-stage-fin"]').find('[data-testid^="preview-any-group-"]').exists()).toBe(false);
  });
  it('a sequence preview has no any-group note', () => {
    expect(mountIt([stage({ code: 'a' }), stage({ code: 'b' })]).find('[data-testid^="preview-any-group-"]').exists()).toBe(false);
  });
});
