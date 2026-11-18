import { afterEach, describe, expect, it, vi } from 'vitest';
import { http } from 'src/services/api/http';
import { buildInfo, clientVersion } from './build-info';
import {
  CLIENT_ERRORS_URL, FIELD_MAX, LIMITS, browserFamily, buildReport, clientFrames, createReporter, isApiError, isChunkLoadError, normalizeMessage,
  sendReport, shouldReport,
  type ClientErrorReport,
} from './error-reporter';
import { recordFailedRequest } from './last-failure';

const route = { matched: [{ path: '/' }, { path: '/dynamic/:moduleKey/:id(\\d+)' }], params: { moduleKey: 'orders', id: '42' } };
const NOW = 1_800_000_000_000;

function reporterWith(send: (r: ClientErrorReport) => Promise<void>) {
  let now = NOW;
  const reporter = createReporter({ send, now: () => now });
  return { reporter, advance: (ms: number) => { now += ms; } };
}

describe('buildReport', () => {
  it('buildReport_fieldsAndTruncation：路由樣板與 moduleKey，不含記錄 id 與查詢字串', () => {
    const e = new TypeError('m'.repeat(1000));
    e.stack = 's'.repeat(6000);
    const r = buildReport('window', e, route, NOW);
    expect(r).toMatchObject({
      kind: 'window', name: 'TypeError', route: '/dynamic/:moduleKey/:id(\\d+)', moduleKey: 'orders',
      version: buildInfo.version, locale: 'zh-TW', occurredAt: new Date(NOW).toISOString(),
    });
    expect(r.message).toHaveLength(LIMITS.messageMax);
    expect(r.stack).toHaveLength(LIMITS.stackMax);
    const json = JSON.stringify(r);
    expect(json).not.toContain('?');
    expect(json).not.toContain('42');
    expect(r.browser).toBe(browserFamily());
  });

  it('沒有路由（啟動早期）時 route、moduleKey 為 null；字串 rejection 照送', () => {
    expect(buildReport('promise', 'boom', null, NOW)).toMatchObject({ name: '', message: 'boom', stack: '', route: null, moduleKey: null });
  });

  it('非 Error 物件不序列化內容，只送固定技術訊息（含敏感欄位的物件不外送）', () => {
    const secret = { token: 'fwp_secret123', recordId: 4242, url: 'https://x/dynamic/orders/4242?q=pw' };
    const r = buildReport('promise', secret, null, NOW);
    expect(r.message).toBe('非 Error 物件：Object');
    const json = JSON.stringify(r);
    for (const leak of ['fwp_secret123', '4242', 'q=pw']) expect(json).not.toContain(leak);
    class Weird { secret = 'x1y2'; }
    expect(buildReport('promise', new Weird(), null, NOW).message).toBe('非 Error 物件：Weird');
    expect(buildReport('promise', Object.create(null), null, NOW).message).toBe('非 Error 物件：Object');
    expect(buildReport('promise', 42, null, NOW).message).toBe('非 Error 物件：number');
  });

  it('超長 metadata 套後端欄位上限，最終 UTF-8 JSON 不超過上限', () => {
    const e = Object.assign(new Error('m'), { name: 'N'.repeat(5000) });
    const longRoute = { matched: [{ path: '/' + 'r'.repeat(5000) }], params: { moduleKey: 'k'.repeat(5000) } };
    const r = buildReport('vue', e, longRoute, NOW);
    expect(r.name).toHaveLength(FIELD_MAX.name);
    expect(r.route).toHaveLength(FIELD_MAX.route);
    expect(r.moduleKey).toHaveLength(FIELD_MAX.moduleKey);
    expect(r.version.length).toBeLessThanOrEqual(FIELD_MAX.version);
    expect(r.locale.length).toBeLessThanOrEqual(FIELD_MAX.locale);
    expect(r.browser.length).toBeLessThanOrEqual(FIELD_MAX.browser);
    expect(new TextEncoder().encode(JSON.stringify(r)).length).toBeLessThanOrEqual(LIMITS.maxBytes);
  });

  it('JSON 跳脫（大量引號與控制字元）後仍不超過上限', () => {
    const e = new Error('"\u0001'.repeat(250));
    e.stack = '\u0000"\\'.repeat(1400);
    const r = buildReport('vue', e, route, NOW);
    expect(new TextEncoder().encode(JSON.stringify(r)).length).toBeLessThanOrEqual(LIMITS.maxBytes);
    expect(r.message.length).toBeGreaterThan(0);
  });

  it('buildReport_includesRecentFailedRequestIdWithin30s', () => {
    const rid = 'c'.repeat(32);
    recordFailedRequest(rid, NOW);
    expect(buildReport('vue', new Error('x'), route, NOW + 30_000).lastRequestId).toBe(rid);
    expect(buildReport('vue', new Error('x'), route, NOW + 30_001)).not.toHaveProperty('lastRequestId');
  });

  it('buildReport_cjkMessage_fitsBodyLimit：先縮 stack、訊息保留', () => {
    const e = new Error('錯'.repeat(500));
    e.stack = '堆疊'.repeat(2000);
    const r = buildReport('vue', e, route, NOW);
    expect(new TextEncoder().encode(JSON.stringify(r)).length).toBeLessThanOrEqual(LIMITS.maxBytes);
    expect(r.message).toHaveLength(500);
    expect(r.stack.length).toBeLessThan(4000);
  });
});

describe('版本與瀏覽器', () => {
  it('沒注入時是 dev；有 commit 時 version 接前 12 碼', () => {
    expect(buildInfo).toEqual({ version: 'dev', commit: 'dev', builtAt: 'dev' });
    expect(clientVersion()).toBe('dev');
    expect(clientVersion({ version: '1.2.0', commit: '0123456789abcdef' })).toBe('1.2.0+0123456789ab');
  });

  it('瀏覽器只取家族與主版本', () => {
    expect(browserFamily('Mozilla/5.0 (Macintosh) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36')).toBe('Chrome 131');
    expect(browserFamily('Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36 Edg/131.0.2903.70')).toBe('Edge 131');
    expect(browserFamily('Mozilla/5.0 (Windows NT 10.0; rv:133.0) Gecko/20100101 Firefox/133.0')).toBe('Firefox 133');
    expect(browserFamily('Mozilla/5.0 (iPad; CPU OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1')).toBe('Safari 17');
    expect(browserFamily('curl/8')).toBe('Other');
  });
});

describe('判斷', () => {
  it('isChunkLoadError_detectsViteMessages', () => {
    expect(isChunkLoadError(new TypeError('Failed to fetch dynamically imported module: https://x/assets/ListPage-abc.js'))).toBe(true);
    expect(isChunkLoadError(new TypeError('Importing a module script failed.'))).toBe(true);
    expect(isChunkLoadError(new TypeError('error loading dynamically imported module: https://x/a.js'))).toBe(true);
    expect(isChunkLoadError(Object.assign(new Error('x'), { name: 'ChunkLoadError' }))).toBe(true);
    expect(isChunkLoadError(new Error('Unable to preload CSS for /assets/ListPage-abc12345.css'))).toBe(true);
    expect(isChunkLoadError(new Error('x is undefined'))).toBe(false);
  });

  it('isApiError_skipsApiRejections', () => {
    expect(isApiError({ status: 500, message: 'x' })).toBe(true);
    expect(isApiError({ status: 0, code: 'network' })).toBe(true);
    expect(isApiError(new Error('x'))).toBe(false);
    expect(isApiError('x')).toBe(false);
  });

  it('shouldReport_skipsApiErrorForVueAndPromise', () => {
    expect(shouldReport('vue', { status: 500, message: 'x' })).toBe(false);
    expect(shouldReport('promise', { status: 500, message: 'x' })).toBe(false);
    expect(shouldReport('vue', new Error('x'))).toBe(true);
    expect(shouldReport('promise', new Error('x'))).toBe(true);
  });

  it('shouldReport_ignoresResizeObserverNoise', () => {
    expect(shouldReport('window', 'ResizeObserver loop completed with undelivered notifications.')).toBe(false);
  });
});

describe('createReporter', () => {
  it('report_sameFingerprintWithin5Min_sentOnce（邊界：299_999 不送、300_000 送）', () => {
    const send = vi.fn(async () => {});
    const { reporter, advance } = reporterWith(send);
    for (let i = 0; i < 3; i++) reporter.report('window', new Error('same'), route);
    expect(send).toHaveBeenCalledTimes(1);
    advance(LIMITS.dedupeMs - 1);
    reporter.report('window', new Error('same'), route);
    expect(send).toHaveBeenCalledTimes(1);
    advance(1);
    reporter.report('window', new Error('same'), route);
    expect(send).toHaveBeenCalledTimes(2);
  });

  it('只差數字、id、引號字串或行列號的同一錯誤算同一指紋', () => {
    const send = vi.fn(async () => {});
    const { reporter } = reporterWith(send);
    const at = (msg: string, line: number, hash: string) => Object.assign(new Error(msg), {
      stack: `Error: ${msg}\n    at loadRow (https://x/assets/ListPage-${hash}.js:${line}:17)\n    at https://x/assets/index-${hash}.js:3:9`,
    });
    reporter.report('window', at('row 12 of "a@b.com" 0123abcd99 missing', 10, 'AbCd1234'), route);
    reporter.report('window', at('row 873 of "z@y.org" ffee0011aa missing', 99, 'ZzYy9876'), route);
    expect(send).toHaveBeenCalledTimes(1);
    reporter.report('window', at('row 1 other', 10, 'AbCd1234'), route);
    expect(send).toHaveBeenCalledTimes(2);
  });

  it('同一個錯誤物件從兩個入口進來只回報一次', () => {
    const send = vi.fn(async () => {});
    const { reporter, advance } = reporterWith(send);
    const e = new Error('Unable to preload CSS for /assets/x.css');
    reporter.report('chunk', e, route);
    advance(LIMITS.dedupeMs);
    reporter.report('chunk', e, route);
    expect(send).toHaveBeenCalledTimes(1);
  });

  it('不同種類或不同路由不算同一個', () => {
    const send = vi.fn(async () => {});
    const { reporter } = reporterWith(send);
    reporter.report('window', new Error('same'), route);
    reporter.report('promise', new Error('same'), route);
    reporter.report('window', new Error('same'), { matched: [{ path: '/tasks' }], params: {} });
    expect(send).toHaveBeenCalledTimes(3);
  });

  it('report_cappedAt10PerMinute', () => {
    const send = vi.fn(async () => {});
    const { reporter, advance } = reporterWith(send);
    for (let i = 0; i < 12; i++) reporter.report('window', new Error(`e${'x'.repeat(i)}`), route);
    expect(send).toHaveBeenCalledTimes(LIMITS.perMinute);
    advance(59_999);
    reporter.report('window', new Error('still capped'), route);
    expect(send).toHaveBeenCalledTimes(LIMITS.perMinute);
    advance(1);
    reporter.report('window', new Error('later'), route);
    expect(send).toHaveBeenCalledTimes(LIMITS.perMinute + 1);
  });

  it('send_failure_doesNotReport：送出失敗（rejected 或同步丟例外）不丟例外、不遞迴回報', async () => {
    const rejecting = vi.fn(() => Promise.reject(new Error('offline')));
    const throwing = vi.fn((): Promise<void> => { throw new Error('sync'); });
    expect(() => reporterWith(rejecting).reporter.report('window', new Error('a'), route)).not.toThrow();
    expect(() => reporterWith(throwing).reporter.report('window', new Error('a'), route)).not.toThrow();
    await Promise.resolve();
    expect(rejecting).toHaveBeenCalledTimes(1);
    expect(throwing).toHaveBeenCalledTimes(1);
  });

  it('API 失敗不回報', () => {
    const send = vi.fn(async () => {});
    reporterWith(send).reporter.report('promise', { status: 500, message: 'x' }, route);
    expect(send).not.toHaveBeenCalled();
  });
});

describe('sendReport', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('sendReport_usesFetchKeepalive_notAxios', async () => {
    const fetchMock = vi.fn(async () => new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', fetchMock);
    const adapter = vi.fn();
    http.defaults.adapter = adapter;
    const r = buildReport('window', new Error('x'), route, NOW);
    await sendReport(r);
    expect(fetchMock).toHaveBeenCalledWith(CLIENT_ERRORS_URL, expect.objectContaining({ method: 'POST', keepalive: true, body: JSON.stringify(r) }));
    expect(adapter).not.toHaveBeenCalled();
  });

  it('fetch 失敗時吞掉，不丟例外', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => { throw new TypeError('Failed to fetch'); }));
    await expect(sendReport(buildReport('window', new Error('x'), route, NOW))).resolves.toBeUndefined();
  });
});

describe('與後端指紋相同的正規化', () => {
  it('normalizeMessage 對照後端 ErrorFingerprint.NormalizeMessage', () => {
    expect(normalizeMessage(`id 42 user 'bob' mail a.b@c.io guid 0f8fad5b-d9cb-469f-a165-70867728950e hex deadbeef01`))
      .toBe('id {n} user {s} mail {email} guid {guid} hex {hex}');
    expect(normalizeMessage('x'.repeat(300))).toHaveLength(200);
  });

  it('clientFrames 取三個框、去掉行列號與 Vite 雜湊', () => {
    const stack = [
      'TypeError: x', '    at loadRow (https://x/assets/ListPage-AbCd1234.js:10:5)', '    at https://x/assets/index-Zz_y-987.js?v=1:3:9',
      'save@https://x/assets/FormPage-Qw3r5t6y.js:1:2', '    at fourth (https://x/a.js:1:1)',
    ].join('\n');
    expect(clientFrames(stack)).toEqual(['loadRow@ListPage.js', '?@index.js', 'save@FormPage.js']);
  });
});
