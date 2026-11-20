import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import type { LocaleAdminEntry } from 'src/domain/dynamic/locale-pack-types';
import { HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import LocaleTemplateExportDialog from './LocaleTemplateExportDialog.vue';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });

const section = (o: Record<string, unknown> = {}) => ({
  baseChanged: 0, paramsChanged: 0, runtimeFallback: 0, orphaned: 0,
  baseChangedKeys: [], paramsChangedKeys: [], runtimeFallbackKeys: [], orphanedKeys: [], ...o,
});
const entry = (o: Partial<LocaleAdminEntry> & { code: string; nativeName: string }): LocaleAdminEntry => ({
  origin: 'builtin', fallbackCode: null, enabled: true, isDefault: false, required: false, overrides: { ui: 0, server: 0 }, current: null, hasPrevious: false,
  completeness: { ui: { have: 10, total: 10 }, server: { have: 5, total: 5 }, topMissing: [] },
  review: { ui: section(), server: section() }, builtFor: null, ...o,
});
const LOCALES = [
  entry({ code: 'zh-TW', nativeName: '繁體中文', isDefault: true, required: true }),
  entry({ code: 'en-US', nativeName: 'English' }),
  entry({ code: 'th', nativeName: 'ไทย' }),
];
const withReview = (ui: Record<string, unknown>, server: Record<string, unknown> = {}) =>
  entry({ code: 'th', nativeName: 'ไทย', review: { ui: section(ui), server: section(server) } });

const MB = 1048576;
let calls: Call[];
const mounted: { unmount(): void }[] = [];

async function mountDialog(target: LocaleAdminEntry | null, opts: { reply?: (c: Call) => unknown; packMaxBytes?: number } = {}) {
  calls = mockHttp((c) => {
    const custom = opts.reply?.(c);
    if (custom !== undefined) return custom;
    if (c.url.endsWith('/template')) return new Blob(['{}']);
    return {};
  });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(LocaleTemplateExportDialog, {
    attachTo: document.body,
    props: { modelValue: true, target, locales: LOCALES, packMaxBytes: opts.packMaxBytes ?? 5 * MB },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], createPinia(), i18n] },
  });
  mounted.push(w);
  await flushPromises();
  return w;
}
afterEach(() => { for (const w of mounted.splice(0)) w.unmount(); vi.restoreAllMocks(); });

const q = (id: string) => document.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
const tick = async () => { await new Promise((r) => setTimeout(r, 50)); await flushPromises(); };
const isDisabled = (el: HTMLElement | null) => el?.getAttribute('aria-disabled') === 'true' || el?.classList.contains('disabled') || el?.hasAttribute('disabled');
const templateCalls = () => calls.filter((c) => c.url.endsWith('/template'));
async function download() { q('export-download')!.click(); await tick(); }
async function typeInto(id: string, value: string) {
  // QInput 把 data-testid 放在原生 input 上
  const input = document.querySelector(`input[data-testid="${id}"]`) as HTMLInputElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
  await tick();
}

describe('LocaleTemplateExportDialog 從卡片開啟', () => {
  it('預設參數：兩段都勾、來源繁中、參考 English、兩個篩選 false；目標語系固定顯示', async () => {
    await mountDialog(LOCALES[2]!);
    expect(q('export-target')!.textContent).toContain('ไทย');
    expect(q('export-target')!.textContent).toContain('th');
    expect(q('export-code')).toBeNull();
    await download();
    expect(templateCalls()).toHaveLength(1);
    expect(templateCalls()[0]!.url).toBe('/i18n/locales/th/template');
    expect(templateCalls()[0]!.params).toEqual({ scopes: ['ui', 'server'], onlyMissing: false, onlyBaseChanged: false, source: 'zh-TW', reference: 'en-US' });
  });

  it('勾選跟著變：只匯出介面文字、只匯出缺的句子', async () => {
    await mountDialog(withReview({ baseChanged: 1 }));
    q('export-scope-server')!.click();
    q('export-only-missing')!.click();
    await tick();
    await download();
    expect(templateCalls()[0]!.params).toMatchObject({ scopes: ['ui'], onlyMissing: true, onlyBaseChanged: false });
  });

  it('參考選「不附」送空字串', async () => {
    const w = await mountDialog(LOCALES[2]!);
    // 從卡片開啟時的下拉順序：原文語系、參考語系
    w.findAllComponents(quasar.QSelect)[1]!.vm.$emit('update:modelValue', '');
    await tick();
    await download();
    expect(templateCalls()[0]!.params).toMatchObject({ reference: '' });
  });

  it('範圍都不勾時「下載」停用', async () => {
    await mountDialog(LOCALES[2]!);
    expect(isDisabled(q('export-download'))).toBe(false);
    q('export-scope-ui')!.click();
    q('export-scope-server')!.click();
    await tick();
    expect(isDisabled(q('export-download'))).toBe(true);
  });

  it('原文已更新與參數不符都是 0：只匯出變動句子的選項停用', async () => {
    await mountDialog(LOCALES[2]!);
    expect(isDisabled(q('export-only-changed'))).toBe(true);
  });

  it('只有參數不符（baseChanged 0、paramsChanged 1）：可勾，送出 onlyBaseChanged=true', async () => {
    await mountDialog(withReview({}, { paramsChanged: 1 }));
    expect(isDisabled(q('export-only-changed'))).toBe(false);
    q('export-only-changed')!.click();
    await tick();
    await download();
    expect(templateCalls()[0]!.params).toMatchObject({ onlyBaseChanged: true });
  });

  it('只有原文已更新：可勾', async () => {
    await mountDialog(withReview({ baseChanged: 2 }));
    expect(isDisabled(q('export-only-changed'))).toBe(false);
  });
});

describe('LocaleTemplateExportDialog 新語系樣板', () => {
  it('多送 nativeName、fallback、quasarLang；代碼格式不對照常送出並顯示後端 400 訊息', async () => {
    const w = await mountDialog(null, {
      reply: (c) => (c.url.endsWith('/template') ? new HttpFail(400, { code: 'localePackInvalid', errors: { file: ['語系代碼格式不對'] } }) : undefined),
    });
    expect(q('export-code')).not.toBeNull();
    expect(isDisabled(q('export-only-changed'))).toBe(true);
    await typeInto('export-code', 'zz_BAD');
    await typeInto('export-native-name', 'Bahasa');
    // 新語系樣板的下拉順序：上層語系、原文語系、參考語系
    w.findAllComponents(quasar.QSelect)[0]!.vm.$emit('update:modelValue', 'en-US');
    await typeInto('export-quasar-lang', 'id');
    await download();
    expect(templateCalls()[0]!.url).toBe('/i18n/locales/zz_BAD/template');
    expect(templateCalls()[0]!.params).toMatchObject({ nativeName: 'Bahasa', fallback: 'en-US', quasarLang: 'id', scopes: ['ui', 'server'] });
    expect(q('export-error')!.textContent).toContain('語系代碼格式不對');
    expect(q('export-dialog')).not.toBeNull();
  });

  it('代碼空白時「下載」停用（送出一定會被擋）', async () => {
    await mountDialog(null);
    expect(isDisabled(q('export-download'))).toBe(true);
    await typeInto('export-code', 'id');
    expect(isDisabled(q('export-download'))).toBe(false);
  });
});

describe('LocaleTemplateExportDialog 下載', () => {
  function spyDownload() {
    const create = vi.fn(() => 'blob:u');
    vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: create, revokeObjectURL: vi.fn() }));
    const clicked: { download: string; href: string }[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      clicked.push({ download: this.download, href: this.href });
    });
    return { create, clicked };
  }
  const withFile = (blob: Blob) => (c: Call) => (c.url.endsWith('/template') ? blob : undefined);

  it('用 URL.createObjectURL 下載，檔名是回應的檔名；小於上限就關閉', async () => {
    const { create, clicked } = spyDownload();
    const blob = new Blob(['{"a":1}']);
    const w = await mountDialog(LOCALES[2]!, { reply: withFile(blob) });
    await download();
    expect(create).toHaveBeenCalledWith(blob);
    expect(clicked).toEqual([{ download: 'th.langpack.json', href: 'blob:u' }]);
    expect(w.emitted('update:modelValue')?.at(-1)).toEqual([false]);
  });

  it('樣板大於 packMaxBytes：照樣下載、對話框不關，顯示兩個 MB 數字', async () => {
    const { clicked } = spyDownload();
    const blob = new Blob([new Uint8Array(600_000)]);
    const w = await mountDialog(LOCALES[2]!, { reply: withFile(blob), packMaxBytes: 100_000 });
    await download();
    expect(clicked).toHaveLength(1);
    expect(w.emitted('update:modelValue')).toBeUndefined();
    const text = q('export-too-large')!.textContent!;
    expect(text).toContain('0.57');
    expect(text).toContain('0.1');
  });

  it('403 顯示權限不足、不下載', async () => {
    const { clicked } = spyDownload();
    await mountDialog(LOCALES[2]!, { reply: (c) => (c.url.endsWith('/template') ? new HttpFail(403) : undefined) });
    await download();
    expect(clicked).toHaveLength(0);
    expect(q('export-error')!.textContent).toContain('權限');
  });
});
