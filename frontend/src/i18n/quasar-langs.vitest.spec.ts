import { describe, expect, it } from 'vitest';
import quasarLangs from 'shared/quasar-langs.json';
import { BUILTIN_LOCALES } from './locale';

const installed = Object.keys(import.meta.glob('../../node_modules/quasar/lang/*.js'))
  .map((p) => p.slice(p.lastIndexOf('/') + 1).replace(/\.js$/, ''))
  .sort();

describe('shared/quasar-langs.json', () => {
  it('與 node_modules/quasar/lang 的語言包完全相同', () => {
    expect(quasarLangs, 'Quasar 語言包有變動，執行 node scripts/i18n/gen-quasar-langs.mjs').toEqual(installed);
  });

  it('包含每個內建語系的 quasarLang', () => {
    for (const l of BUILTIN_LOCALES) expect(quasarLangs).toContain(l.quasarLang);
  });
});
