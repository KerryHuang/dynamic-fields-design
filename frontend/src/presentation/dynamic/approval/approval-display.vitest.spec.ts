import { describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import enUS from 'shared/i18n/en-US/ui.json';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { badgeColor, badgeText, instanceTitle } from './approval-display';

const { t } = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } }).global;
const en = createI18n({ legacy: false, locale: 'en-US', messages: { 'en-US': enUS } }).global.t;
const summary = (status: string, extra = {}) => ({ status, stageName: null, stageSeq: null, canSubmit: false, ...extra }) as never;

describe('badgeText', () => {
  it('pending names the stage sequence and name', () => {
    expect(badgeText(summary('pending', { stageSeq: 2, stageName: { 'zh-TW': '財務' } }), 'zh-TW', t)).toBe('簽核中（第 2 關／財務）');
  });
  it('pending without a stage falls back to the plain status', () => {
    expect(badgeText(summary('pending'), 'zh-TW', t)).toBe('簽核中');
  });
  it('finished states use their own wording', () => {
    expect(badgeText(summary('approved'), 'zh-TW', t)).toBe('已核准');
    expect(badgeText(summary('rejected'), 'zh-TW', t)).toBe('已退回');
    expect(badgeText(summary('withdrawn'), 'zh-TW', t)).toBe('已撤回');
    expect(badgeText(summary('cancelled'), 'zh-TW', t)).toBe('已終止');
    expect(badgeText(summary('rerouted'), 'zh-TW', t)).toBe('流程已重來，請重新送出');
  });
  it('none shows nothing', () => {
    expect(badgeText(summary('none'), 'zh-TW', t)).toBeNull();
  });

  const stages = (...names: string[]) => names.map((n) => ({ name: { 'zh-TW': n, 'en-US': `${n}-en` }, addSign: false }));
  const parallel = (activeStages: unknown) => summary('pending', { stageSeq: 2, stageName: { 'zh-TW': '法務' }, activeStages });

  it('badgeText lists two parallel stages', () => {
    expect(badgeText(parallel(stages('法務', '財務')), 'zh-TW', t)).toBe('簽核中（法務、財務）');
    expect(badgeText(parallel(stages('法務', '財務')), 'en-US', en)).toBe('In approval (法務-en, 財務-en)');
  });
  it('badgeText shows the first stage and the total for three or more', () => {
    expect(badgeText(parallel(stages('法務', '財務', '稽核')), 'zh-TW', t)).toBe('簽核中（法務等 3 個）');
    expect(badgeText(parallel(stages('Legal', 'Finance', 'Audit')), 'en-US', en)).toBe('In approval (Legal-en + 2 more)');
  });
  it('badgeText marks add-sign items with their host name', () => {
    const added = [{ name: { 'zh-TW': '財務' }, addSign: true }, { name: { 'zh-TW': '法務' }, addSign: true }];
    expect(badgeText(parallel(added), 'zh-TW', t)).toBe('簽核中（財務（加簽）、法務（加簽））');
    expect(badgeText(parallel([{ name: { 'en-US': 'Finance' }, addSign: true }, ...stages('Legal')]), 'en-US', en)).toBe('In approval (Finance (added approver), Legal-en)');
  });
  it('badgeText keeps the numbered text when activeStages is absent', () => {
    expect(badgeText(parallel(undefined), 'zh-TW', t)).toBe('簽核中（第 2 關／法務）');
    expect(badgeText(parallel(null), 'zh-TW', t)).toBe('簽核中（第 2 關／法務）');
    expect(badgeText(parallel(stages('法務')), 'zh-TW', t)).toBe('簽核中（第 2 關／法務）');
  });
  it('badgeText ignores activeStages once the instance is no longer pending', () => {
    expect(badgeText(summary('approved', { activeStages: stages('法務', '財務') }), 'zh-TW', t)).toBe('已核准');
  });
});

describe('badgeColor', () => {
  it('maps to semantic colours', () => {
    expect(badgeColor('pending')).toBe('warning');
    expect(badgeColor('approved')).toBe('positive');
    expect(badgeColor('rejected')).toBe('negative');
    expect(badgeColor('withdrawn')).toBe('muted');
  });
});

describe('instanceTitle', () => {
  it('uses the snapshot when present', () => {
    expect(instanceTitle('採購單 A', { 'zh-TW': '採購' }, 7, 'zh-TW')).toBe('採購單 A');
  });
  it('empty snapshot falls back to module name and record number', () => {
    expect(instanceTitle('', { 'zh-TW': '採購' }, 7, 'zh-TW')).toBe('採購 #7');
    expect(instanceTitle('  ', undefined, 7, 'zh-TW')).toBe('#7');
  });
});
