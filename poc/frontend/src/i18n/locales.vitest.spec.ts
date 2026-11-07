import { describe, expect, it } from 'vitest';
import enUS from './en-US.json';
import th from './th.json';
import vi from './vi.json';
import zhTW from './zh-TW.json';

function leaves(o: unknown, path = ''): Record<string, string> {
  if (typeof o === 'string') return { [path]: o };
  return Object.entries(o as object).reduce((acc, [k, v]) => ({ ...acc, ...leaves(v, path ? `${path}.${k}` : k) }), {});
}
const base = leaves(zhTW);
const en = leaves(enUS);
// {{欄位}}、{'{欄位編號}'} 是給使用者看的範本語法範例，各語言會連名稱一起翻，不算佔位符
const placeholders = (s: string) => (s.replace(/\{\{.*?\}\}|\{'.*?'\}/g, '').match(/\{\w+\}/g) ?? []).sort();

describe('四語系訊息檔', () => {
  it.each([['en-US', enUS], ['th', th], ['vi', vi]])('%s 的鍵與 zh-TW 完全相同（沒有缺、沒有多）', (_name, msgs) => {
    expect(Object.keys(leaves(msgs)).sort()).toEqual(Object.keys(base).sort());
  });

  it.each([['en-US', enUS], ['th', th], ['vi', vi]])('%s 沒有空字串，佔位符與 en-US 一致', (_name, msgs) => {
    for (const [k, v] of Object.entries(leaves(msgs))) {
      expect(v.trim(), k).not.toBe('');
      expect(placeholders(v), k).toEqual(placeholders(en[k]));
    }
  });

  // th／vi 補完前有近 400 句直接放英文。縮寫、格式名、識別字、純佔位符本來就不翻，列在這裡；其他不得與 en-US 相同
  const UNTRANSLATED_OK = new Set([
    'dynamic.export.csv', 'dynamic.export.excel', 'designer.types.email', 'designer.types.url', 'designer.programCode',
    'designer.locales.en-US', 'groups.blockingItem', 'reports.designer.rangeN',
  ]);
  it.each([['th', th], ['vi', vi]])('%s 的文案不得直接放英文（縮寫與格式名除外）', (_name, msgs) => {
    const same = Object.entries(leaves(msgs)).filter(([k, v]) => v === en[k] && !UNTRANSLATED_OK.has(k)).map(([k]) => k);
    expect(same).toEqual([]);
  });
});
