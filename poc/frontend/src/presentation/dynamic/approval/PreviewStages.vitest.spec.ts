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
  it('any-join heading shows once per group above its first item; every item of the group has a badge, the item after the group does not', () => {
    const w = mountIt([
      stage({ code: 'mgr' }),
      stage({ code: 'legal', anyGroup: 'merge' }), stage({ code: 'fin', anyGroup: 'merge' }),
      stage({ code: 'gm' }),
    ]);
    const heads = w.findAll('[data-testid^="preview-any-group-"]');
    expect(heads.map((n) => n.attributes('data-testid'))).toEqual(['preview-any-group-merge']);
    expect(heads[0].text()).toBe('以下分支任一條完成即可');
    // the heading is its own entry placed before the first item, so "below" includes that item
    const html = w.html();
    expect(html.indexOf('preview-any-group-merge')).toBeLessThan(html.indexOf('preview-stage-legal'));
    expect(html.indexOf('preview-any-group-merge')).toBeGreaterThan(html.indexOf('preview-stage-mgr'));
    for (const code of ['legal', 'fin']) expect(w.find(`[data-testid="preview-stage-${code}"]`).find(`[data-testid="preview-any-badge-${code}"]`).text()).toBe('任一分支');
    for (const code of ['mgr', 'gm']) expect(w.find(`[data-testid="preview-any-badge-${code}"]`).exists()).toBe(false);
  });
  it('a nested group is indented deeper than its outer group and the items after it fall back to the outer group', () => {
    const w = mountIt([
      stage({ code: 'a', anyGroup: 'j' }),
      stage({ code: 'p', anyGroup: 'j2' }), stage({ code: 'q', anyGroup: 'j2' }),
      stage({ code: 'b', anyGroup: 'j' }), stage({ code: 'c', anyGroup: 'j' }),
      stage({ code: 'z' }),
    ]);
    expect(w.findAll('[data-testid^="preview-any-group-"]').map((n) => n.attributes('data-testid'))).toEqual(['preview-any-group-j', 'preview-any-group-j2']);
    expect(w.find('[data-testid="preview-any-group-j"]').attributes('style')).toContain('padding-left: 0px');
    expect(w.find('[data-testid="preview-any-group-j2"]').attributes('style')).toContain('padding-left: 16px');
    expect(w.findAll('[data-testid^="preview-any-badge-"]').map((n) => n.attributes('data-testid')))
      .toEqual(['a', 'p', 'q', 'b', 'c'].map((c) => `preview-any-badge-${c}`));
    // inner and outer groups use different colours
    expect(w.find('[data-testid="preview-any-badge-p"]').classes().join(' ')).not.toBe(w.find('[data-testid="preview-any-badge-a"]').classes().join(' '));
    expect(w.find('[data-testid="preview-any-badge-b"]').classes().join(' ')).toBe(w.find('[data-testid="preview-any-badge-a"]').classes().join(' '));
  });
  it('a sequence preview has no any-group note', () => {
    expect(mountIt([stage({ code: 'a' }), stage({ code: 'b' })]).find('[data-testid^="preview-any-group-"]').exists()).toBe(false);
  });
});
