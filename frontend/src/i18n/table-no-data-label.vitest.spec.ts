import { readdirSync, readFileSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

function vueFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) =>
    e.isDirectory() ? vueFiles(join(dir, e.name)) : e.name.endsWith('.vue') ? [join(dir, e.name)] : [],
  );
}

/** 找出沒設 no-data-label 的 <q-table>（否則空清單會顯示 Quasar 預設的英文 "No data available"）。 */
export function findTablesWithoutNoDataLabel(source: string): number {
  return [...source.matchAll(/<q-table\b[^>]*>/g)].filter((m) => !/no-data-label/.test(m[0])).length;
}

describe('findTablesWithoutNoDataLabel', () => {
  it('抓到沒設 no-data-label 的 q-table', () => {
    expect(findTablesWithoutNoDataLabel('<q-table flat\n :rows="r"\n>')).toBe(1);
  });
  it('有設就放行', () => {
    expect(findTablesWithoutNoDataLabel('<q-table flat :no-data-label="t(\'x\')"\n>')).toBe(0);
  });
});

describe('q-table 空清單文字', () => {
  const root = resolve(__dirname, '..');
  it('所有 .vue 的 q-table 都設 no-data-label（走 i18n，不顯示英文預設）', () => {
    const bad = vueFiles(root)
      .filter((f) => findTablesWithoutNoDataLabel(readFileSync(f, 'utf8')) > 0)
      .map((f) => relative(root, f));
    expect(bad).toEqual([]);
  });
});
