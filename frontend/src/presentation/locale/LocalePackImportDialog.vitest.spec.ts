import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { bodyOf, HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import LocalePackImportDialog from './LocalePackImportDialog.vue';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });

const section = (o: Record<string, unknown> = {}) => ({
  added: 0, changed: 0, unchanged: 0, skipped: 0, sameAsBuiltin: 0, extra: 0, extraKeys: [], rejected: 0, rejectedItems: [], ...o,
});
const report = (o: Record<string, unknown> = {}) => ({
  previewId: 'pv-1', expiresUtc: '2026-11-21T04:00:00Z', importEnabled: true, fileName: 'ja.langpack.json', fileHash: 'abc',
  locale: { code: 'ja', isNew: true, builtin: false, nativeName: '日本語', quasarLang: 'ja', fallbackCode: 'en-US', changes: ['nativeName', 'fallback'], ignored: [] },
  pack: { version: '1.2.0', builtFor: null, baseLocale: 'zh-TW', note: null },
  ui: section({ added: 30, rejected: 2, rejectedItems: [
    { key: 'approval.stagesMore', reason: 'placeholder', args: ['{first}', '{rest}'] },
    { key: 'report.No', reason: 'booleanCollision', args: ['はい', 'ko'] },
  ] }),
  server: section({ added: 4, sameAsBuiltin: 3 }),
  completeness: { ui: { have: 1790, total: 2701 }, server: { have: 160, total: 300 } },
  designData: { count: 12 },
  warnings: [{ code: 'booleanCollisionExisting', args: ['はい', 'ja', 'ko'] }],
  ...o,
});

const FILE_BYTES = new Uint8Array([123, 34, 97, 34, 58, 49, 125]);
let calls: Call[];
let qc: QueryClient;
type Handler = (c: Call) => unknown;

async function mountDialog(handler: Handler = () => undefined, props: Record<string, unknown> = {}) {
  calls = mockHttp((c) => {
    const r = handler(c);
    if (r !== undefined) return r;
    if (c.url === '/i18n/packs/preview') return report();
    if (c.url === '/i18n/packs/apply') return { code: 'ja', isNew: true, noChanges: false, packId: 5, enabled: false };
    if (c.url === '/system/policies') return [{ code: 'locale.enabled', effective: ['zh-TW', 'en-US'] }];
    return {};
  });
  qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(LocalePackImportDialog, {
    attachTo: document.body,
    props: { modelValue: true, importEnabled: true, packMaxBytes: 5242880, ...props },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], createPinia(), i18n, [VueQueryPlugin, { queryClient: qc }]] },
  });
  await flushPromises();
  return w;
}
const q = (id: string) => document.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
const tick = async () => { await new Promise((r) => setTimeout(r, 50)); await flushPromises(); };
async function pick(bytes: Uint8Array = FILE_BYTES, name = 'ja.langpack.json') {
  const input = q('pack-file') as HTMLInputElement;
  const file = new File([bytes as BlobPart], name, { type: 'application/json' });
  Object.defineProperty(file, 'size', { value: bytes.length });
  Object.defineProperty(input, 'files', { value: [file], configurable: true });
  input.dispatchEvent(new Event('change'));
  await tick();
}
const previews = () => calls.filter((c) => c.url === '/i18n/packs/preview');
const keys = (spy: ReturnType<typeof vi.spyOn>) => spy.mock.calls.map((c: unknown[]) => JSON.stringify((c[0] as { queryKey: unknown }).queryKey));

describe('LocalePackImportDialog 預覽', () => {
  it('選檔後送出的本文就是檔案位元組，並帶 fileName', async () => {
    const w = await mountDialog();
    await pick();
    expect(previews()).toHaveLength(1);
    expect(previews()[0].params).toMatchObject({ fileName: 'ja.langpack.json' });
    expect([...new Uint8Array(previews()[0].data as ArrayBuffer)]).toEqual([...FILE_BYTES]);
    w.unmount();
  });

  it('報告：語系、各段計數、完整度百分比、拒收原因文字、警告與設計資料說明', async () => {
    const w = await mountDialog();
    await pick();
    const text = q('pack-report')!.textContent!;
    expect(text).toContain('日本語');
    expect(text).toContain('新安裝');
    expect(q('report-ui-added')!.textContent).toContain('30');
    expect(q('report-ui-rejected')!.textContent).toContain('2');
    expect(q('report-server-sameAsBuiltin')!.textContent).toContain('3');
    expect(q('report-completeness-ui')!.textContent).toContain('1790');
    expect(q('report-completeness-ui')!.textContent).toContain('66%');
    expect(q('report-rejected')!.textContent).toContain('approval.stagesMore');
    expect(q('report-rejected')!.textContent).toContain('{first}');
    expect(q('report-rejected')!.textContent).toContain('ko');
    expect(q('report-warnings')!.textContent).toContain('はい');
    expect(q('report-design-data')!.textContent).toContain('12');
    w.unmount();
  });

  it('報告顯示 builtFor（只顯示、不比對），值照字面呈現', async () => {
    const w = await mountDialog((c) => (c.url === '/i18n/packs/preview'
      ? report({ pack: { version: '1.2.0', builtFor: '<b>2026.11</b>', baseLocale: 'zh-TW', note: null } }) : undefined));
    await pick();
    expect(q('report-built-for')!.textContent).toContain('<b>2026.11</b>');
    expect(q('report-built-for')!.querySelector('b')).toBeNull();
    w.unmount();
  });

  it('沒有 builtFor 時不顯示', async () => {
    const w = await mountDialog();
    await pick();
    expect(q('report-built-for')).toBeNull();
    w.unmount();
  });

  it('拒收清單超過回傳筆數時註明總數', async () => {
    const w = await mountDialog((c) => (c.url === '/i18n/packs/preview'
      ? report({ ui: section({ rejected: 120, rejectedItems: [{ key: 'a.b', reason: 'syntax', args: [] }] }) }) : undefined));
    await pick();
    expect(q('report-rejected')!.textContent).toContain('120');
    w.unmount();
  });

  it('previewId 為 null（這個環境不暫存）：套用停用並顯示說明', async () => {
    const w = await mountDialog((c) => (c.url === '/i18n/packs/preview' ? report({ previewId: null, expiresUtc: null, importEnabled: false }) : undefined),
      { importEnabled: false });
    await pick();
    expect(q('pack-apply')!.getAttribute('disabled') ?? q('pack-apply')!.getAttribute('aria-disabled')).not.toBeNull();
    expect(q('pack-import-disabled')).not.toBeNull();
    w.unmount();
  });

  it('超過 packMaxBytes 不呼叫預覽，顯示大小上限', async () => {
    const w = await mountDialog(() => undefined, { packMaxBytes: 4 });
    await pick();
    expect(previews()).toHaveLength(0);
    expect(q('pack-error')!.textContent).toContain('不能超過');
    w.unmount();
  });

  it('整包錯誤 400 顯示 errors.file 的文字；413 顯示大小上限', async () => {
    const w = await mountDialog(() => new HttpFail(400, { code: 'localePackInvalid', errors: { file: ['不收由右至左的語系'] } }));
    await pick();
    expect(q('pack-error')!.textContent).toContain('不收由右至左的語系');
    w.unmount();
    const w2 = await mountDialog(() => new HttpFail(413));
    await pick();
    expect(q('pack-error')!.textContent).toContain('不能超過');
    w2.unmount();
  });
});

describe('LocalePackImportDialog 套用', () => {
  it('套用送 previewId 與 fileHash；成功後失效三個 query key，新語系未啟用時出現「現在啟用」', async () => {
    const w = await mountDialog();
    await pick();
    const spy = vi.spyOn(qc, 'invalidateQueries');
    q('pack-apply')!.click();
    await tick();
    const apply = calls.find((c) => c.url === '/i18n/packs/apply')!;
    expect(bodyOf(apply)).toEqual({ previewId: 'pv-1', fileHash: 'abc' });
    for (const k of ['["locale-admin"]', '["effective-policies"]', '["auth-options"]']) expect(keys(spy)).toContain(k);
    expect(q('pack-done')).not.toBeNull();
    expect(q('pack-enable')).not.toBeNull();
    w.unmount();
  });

  it('「現在啟用」把新語系加進 locale.enabled 的現有清單', async () => {
    const w = await mountDialog();
    await pick();
    q('pack-apply')!.click();
    await tick();
    q('pack-enable')!.click();
    await tick();
    const put = calls.find((c) => c.method === 'put' && c.url === '/system/policies/locale.enabled')!;
    expect(bodyOf(put)).toEqual({ value: ['zh-TW', 'en-US', 'ja'] });
    expect(q('pack-enable')).toBeNull();
    w.unmount();
  });

  it('已經啟用的語系沒有「現在啟用」', async () => {
    const w = await mountDialog((c) => (c.url === '/i18n/packs/apply' ? { code: 'th', isNew: false, noChanges: false, packId: 7, enabled: true } : undefined));
    await pick();
    q('pack-apply')!.click();
    await tick();
    expect(q('pack-enable')).toBeNull();
    w.unmount();
  });

  it('套用 409 過期：顯示後端訊息與「重新預覽」，按下後用同一個檔再預覽一次', async () => {
    const w = await mountDialog((c) => (c.url === '/i18n/packs/apply'
      ? new HttpFail(409, { code: 'localePackStale', message: '語系在預覽之後被變更了，請重新上傳預覽' }) : undefined));
    await pick();
    q('pack-apply')!.click();
    await tick();
    expect(q('pack-error')!.textContent).toContain('語系在預覽之後被變更了');
    expect(previews()).toHaveLength(1);
    q('pack-repreview')!.click();
    await tick();
    expect(previews()).toHaveLength(2);
    expect([...new Uint8Array(previews()[1].data as ArrayBuffer)]).toEqual([...FILE_BYTES]);
    w.unmount();
  });
});

describe('LocalePackImportDialog 換檔與關閉時作廢舊流程（第二輪審查必修 1）', () => {
  const deferred = <T>() => {
    let resolve!: (v: T) => void; let reject!: (e: unknown) => void;
    const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
    return { promise, resolve, reject };
  };
  const reportFor = (id: string) => report({ previewId: `pv-${id}`, fileHash: `hash-${id}`, fileName: `${id}.json`,
    locale: { code: 'ja', isNew: true, builtin: false, nativeName: `語系${id}`, quasarLang: 'ja', fallbackCode: 'en-US', changes: [], ignored: [] } });
  const BYTES_A = new Uint8Array([1]);
  const BYTES_B = new Uint8Array([2]);
  const firstByte = (c: Call) => new Uint8Array(c.data as ArrayBuffer)[0];

  it('先選 A 再選 B，B 先回、A 後回：報告與套用都是 B', async () => {
    const waits = { 1: deferred<unknown>(), 2: deferred<unknown>() } as Record<number, ReturnType<typeof deferred<unknown>>>;
    const w = await mountDialog((c) => (c.url === '/i18n/packs/preview' ? waits[firstByte(c)]!.promise : undefined));
    await pick(BYTES_A, 'A.json');
    await pick(BYTES_B, 'B.json');
    waits[2]!.resolve(reportFor('B'));
    await tick();
    waits[1]!.resolve(reportFor('A'));
    await tick();
    expect(q('pack-report')!.textContent).toContain('語系B');
    expect(q('pack-report')!.textContent).not.toContain('語系A');
    q('pack-apply')!.click();
    await tick();
    expect(bodyOf(calls.find((c) => c.url === '/i18n/packs/apply'))).toEqual({ previewId: 'pv-B', fileHash: 'hash-B' });
    w.unmount();
  });

  it('A 的預覽在選 B 之後才失敗：不顯示 A 的錯誤', async () => {
    const waits = { 1: deferred<unknown>(), 2: deferred<unknown>() } as Record<number, ReturnType<typeof deferred<unknown>>>;
    const w = await mountDialog((c) => (c.url === '/i18n/packs/preview' ? waits[firstByte(c)]!.promise : undefined));
    await pick(BYTES_A, 'A.json');
    await pick(BYTES_B, 'B.json');
    waits[2]!.resolve(reportFor('B'));
    await tick();
    waits[1]!.reject(new Error('A 壞了'));
    await tick();
    expect(q('pack-error')).toBeNull();
    expect(q('pack-report')!.textContent).toContain('語系B');
    w.unmount();
  });

  it('A 還在讀檔時換成 B：只預覽 B', async () => {
    const w = await mountDialog((c) => (c.url === '/i18n/packs/preview' ? reportFor(firstByte(c) === 1 ? 'A' : 'B') : undefined));
    const input = q('pack-file') as HTMLInputElement;
    const reading = deferred<ArrayBuffer>();
    const fileA = new File([BYTES_A as BlobPart], 'A.json', { type: 'application/json' });
    Object.defineProperty(fileA, 'arrayBuffer', { value: () => reading.promise });
    Object.defineProperty(input, 'files', { value: [fileA], configurable: true });
    input.dispatchEvent(new Event('change'));
    await tick();
    await pick(BYTES_B, 'B.json');
    reading.resolve(BYTES_A.buffer as ArrayBuffer);
    await tick();
    expect(previews()).toHaveLength(1);
    expect(firstByte(previews()[0]!)).toBe(2);
    expect(q('pack-report')!.textContent).toContain('語系B');
    expect(document.body.textContent).toContain('B.json');
    expect(document.body.textContent).not.toContain('A.json');
    w.unmount();
  });

  it('預覽中關閉再打開：舊流程的結果不寫回', async () => {
    const wait = deferred<unknown>();
    const w = await mountDialog((c) => (c.url === '/i18n/packs/preview' ? wait.promise : undefined));
    await pick(BYTES_A, 'A.json');
    await w.setProps({ modelValue: false });
    await tick();
    await w.setProps({ modelValue: true });
    await tick();
    wait.resolve(reportFor('A'));
    await tick();
    expect(q('pack-report')).toBeNull();
    expect(q('pack-loading')).toBeNull();
    w.unmount();
  });

  it('套用中關閉再打開：舊套用的結果不寫回', async () => {
    const wait = deferred<unknown>();
    const w = await mountDialog((c) => (c.url === '/i18n/packs/apply' ? wait.promise : undefined));
    await pick();
    q('pack-apply')!.click();
    await tick();
    await w.setProps({ modelValue: false });
    await tick();
    await w.setProps({ modelValue: true });
    await tick();
    wait.resolve({ code: 'ja', isNew: true, noChanges: false, packId: 5, enabled: false });
    await tick();
    expect(q('pack-done')).toBeNull();
    w.unmount();
  });

  it('預覽與套用進行中停用換檔', async () => {
    const previewWait = deferred<unknown>();
    const applyWait = deferred<unknown>();
    const w = await mountDialog((c) => (c.url === '/i18n/packs/preview' ? previewWait.promise : c.url === '/i18n/packs/apply' ? applyWait.promise : undefined));
    const disabled = () => q('pack-pick')!.getAttribute('disabled') ?? q('pack-pick')!.getAttribute('aria-disabled');
    await pick();
    expect(disabled()).not.toBeNull();
    previewWait.resolve(reportFor('A'));
    await tick();
    expect(disabled()).toBeNull();
    q('pack-apply')!.click();
    await tick();
    expect(disabled()).not.toBeNull();
    applyWait.resolve({ code: 'ja', isNew: true, noChanges: false, packId: 5, enabled: true });
    await tick();
    w.unmount();
  });
});
