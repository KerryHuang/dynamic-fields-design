import { describe, expect, it } from 'vitest';
import { resolveI18n } from './i18n-text';

describe('resolveI18n', () => {
  it('returns the requested locale when present', () => {
    expect(resolveI18n({ 'zh-TW': '客戶', 'en-US': 'Customer' }, 'en-US')).toBe('Customer');
  });
  it('falls back to zh-TW for missing locale', () => {
    expect(resolveI18n({ 'zh-TW': '客戶' }, 'th')).toBe('客戶');
  });
  it('falls back for empty string', () => {
    expect(resolveI18n({ 'zh-TW': '客戶', 'en-US': '' }, 'en-US')).toBe('客戶');
  });
  it('returns empty string when nothing is available', () => {
    expect(resolveI18n({}, 'vi')).toBe('');
  });
});
