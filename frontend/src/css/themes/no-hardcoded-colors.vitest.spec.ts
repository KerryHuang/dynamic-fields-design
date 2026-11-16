import { readdirSync, readFileSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

const PALETTE =
  'red|pink|purple|deep-purple|indigo|blue-grey|light-blue|blue|cyan|teal|light-green|green|lime|yellow|amber|deep-orange|orange|brown|grey|white|black';
const SEMANTIC = new Set(['primary', 'secondary', 'accent', 'dark', 'positive', 'negative', 'info', 'warning', 'muted']);
const COLOR_ATTRS = 'color|text-color|bg-color|label-color|active-color|indicator-color|toggle-color|toggle-text-color';
const COLOR_FN = /#[0-9a-f]{3,8}\b|\b(?:rgb|rgba|hsl|hsla)\(/gi;

/**
 * 找出 .vue 原始碼裡寫死的顏色，回傳命中的片段。
 * 寫死 = Quasar 調色盤 class、非語意的顏色屬性、行內 style 的顏色、<style> 裡的 hex／rgb／hsl。
 * hex 只在 style 屬性與 <style> 區塊內檢查，因為 <template #add>、#body-cell-name 也長得像 hex。
 */
export function findHardcodedColors(source: string): string[] {
  const found: string[] = [];

  for (const m of source.matchAll(new RegExp(`(?<![\\w-])(?:text|bg)-(?:${PALETTE})(?:-\\d+)?(?![\\w-])`, 'g'))) found.push(m[0]);

  for (const m of source.matchAll(new RegExp(`(?<![\\w-])(:?)(?:${COLOR_ATTRS})="([^"]*)"`, 'g'))) {
    if (m[1]) {
      for (const lit of m[2].matchAll(/'([^']*)'/g)) if (!SEMANTIC.has(lit[1])) found.push(lit[1]);
    } else if (!SEMANTIC.has(m[2])) found.push(m[0]);
  }

  for (const m of source.matchAll(/(:?)style="([^"]*)"/g)) {
    for (const c of m[2].matchAll(COLOR_FN)) found.push(c[0]);
    if (m[1]) continue;
    for (const d of m[2].split(';')) {
      const [prop, ...rest] = d.split(':');
      const value = rest.join(':').trim();
      if (/^\s*(color|background|background-color|border-color)\s*$/.test(prop) && value && !value.startsWith('var(')) found.push(d.trim());
    }
  }

  for (const block of source.matchAll(/<style[^>]*>([\s\S]*?)<\/style>/g)) {
    for (const c of block[1].matchAll(COLOR_FN)) found.push(c[0]);
  }
  return found;
}

describe('findHardcodedColors', () => {
  it.each([
    'class="text-grey-7"',
    ':class="{ \'bg-red-1\': bad }"',
    'color="grey-6"',
    ':color="a ? \'grey\' : \'primary\'"',
    'style="color: red"',
    'style="background:#fff"',
    '<style scoped>.x{color:#abc}</style>',
    'class="text-blue-grey-5"',
    'class="q-pa-md text-white"',
  ])('會抓到 %s', (src) => {
    expect(findHardcodedColors(src).length).toBeGreaterThan(0);
  });

  it.each([
    'class="text-primary"',
    'class="bg-negative"',
    'color="primary"',
    'color="muted"',
    ':color="a ? \'negative\' : undefined"',
    'style="height: 360px"',
    'style="color: var(--t-muted)"',
    '<template #add>',
    '<template #body-cell-name>',
    '<style scoped>.x{border-left:3px solid var(--q-negative)}</style>',
    'class="text-weight-bold text-caption text-h6"',
  ])('不會誤抓 %s', (src) => {
    expect(findHardcodedColors(src)).toEqual([]);
  });
});

function vueFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) =>
    e.isDirectory() ? vueFiles(join(dir, e.name)) : e.name.endsWith('.vue') ? [join(dir, e.name)] : [],
  );
}

const SRC = resolve(process.cwd(), 'src');
/** 允許保留的固定組合；bg-negative 上的白字由 quasar-bridge.css 依主題覆寫成 --t-on-negative。 */
const EXCEPTIONS: Record<string, string[]> = { 'layouts/MainLayout.vue': ['text-white'] };

describe('原始碼', () => {
  const files = vueFiles(SRC).map((f) => [relative(SRC, f), readFileSync(f, 'utf8')] as const);

  it('src 底下所有 .vue 都沒有寫死的顏色（例外清單除外）', () => {
    const offenders = files
      .map(([name, src]) => [name, findHardcodedColors(src).filter((c) => !(EXCEPTIONS[name] ?? []).includes(c))] as const)
      .filter(([, hits]) => hits.length > 0)
      .map(([name, hits]) => `${name}: ${[...new Set(hits)].join(', ')}`);
    expect(offenders).toEqual([]);
  });

  it('例外清單沒有過期', () => {
    for (const [name, hits] of Object.entries(EXCEPTIONS)) {
      const src = files.find(([n]) => n === name)?.[1] ?? '';
      for (const h of hits) expect(findHardcodedColors(src), `${name} 不再有 ${h}`).toContain(h);
    }
  });
});
