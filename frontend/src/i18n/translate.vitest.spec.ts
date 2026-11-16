import { afterEach, describe, expect, it } from 'vitest';
import { i18n } from 'src/boot/i18n';
import { joinList, tFor } from './translate';

describe('translate', () => {
  afterEach(() => { i18n.global.locale.value = 'zh-TW'; });

  it('joinList 依語系選分隔符', () => {
    expect(joinList(['甲', '乙', '丙'], 'zh-TW')).toBe('甲、乙、丙');
    expect(joinList(['A', 'B', 'C'], 'en-US')).toBe('A, B, C');
    expect(joinList(['A', 'B'], 'th')).toBe('A, B');
    expect(joinList(['A', 'B'], 'vi')).toBe('A, B');
  });

  it('joinList 不給語系時用目前介面語系', () => {
    expect(joinList(['a', 'b'])).toBe('a、b');
    i18n.global.locale.value = 'en-US';
    expect(joinList(['a', 'b'])).toBe('a, b');
  });

  it('tFor 指定語系並帶參數', () => {
    expect(tFor('en-US')('dynamic.validation.maxLength', { n: 5 })).toBe('Exceeds 5 characters');
  });
});
