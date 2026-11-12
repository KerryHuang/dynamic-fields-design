import { describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'src/i18n/zh-TW.json';
import { badgeColor, badgeText } from './approval-display';

const { t } = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } }).global;
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
});

describe('badgeColor', () => {
  it('maps to semantic colours', () => {
    expect(badgeColor('pending')).toBe('warning');
    expect(badgeColor('approved')).toBe('positive');
    expect(badgeColor('rejected')).toBe('negative');
    expect(badgeColor('withdrawn')).toBe('muted');
  });
});
