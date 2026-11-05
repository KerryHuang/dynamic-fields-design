import { createPinia, setActivePinia } from 'pinia';
import { beforeEach, describe, expect, it } from 'vitest';
import { dynamicApi } from './dynamic-api';
import { http } from './http';

describe('dynamicApi.getPagedRecords', () => {
  beforeEach(() => setActivePinia(createPinia()));

  it('serializes repeated sortBy/sortOrder with f_ keys', async () => {
    let url = '';
    http.defaults.adapter = async (config) => {
      url = http.getUri(config);
      return { data: {}, status: 200, statusText: 'OK', headers: {}, config };
    };
    await dynamicApi.getPagedRecords('crm', {
      currentPage: 2,
      pagesSize: 20,
      filter: '{"field":3,"op":"eq","value":"a"}',
      sort: [{ field: 3, order: 'asc' }, { field: 5, order: 'desc' }],
      q: 'abc',
    });
    const params = new URL(url, 'http://x').searchParams;
    expect(url).toContain('/modules/crm/records/paged?');
    expect(params.get('currentPage')).toBe('2');
    expect(params.get('pagesSize')).toBe('20');
    expect(params.get('filter')).toBe('{"field":3,"op":"eq","value":"a"}');
    expect(params.getAll('sortBy')).toEqual(['f_3', 'f_5']);
    expect(params.getAll('sortOrder')).toEqual(['asc', 'desc']);
    expect(params.get('q')).toBe('abc');
    expect(url).not.toContain('[');
    expect(url).not.toContain('%5B');
  });

  it('omits optional params when absent', async () => {
    let url = '';
    http.defaults.adapter = async (config) => {
      url = http.getUri(config);
      return { data: {}, status: 200, statusText: 'OK', headers: {}, config };
    };
    await dynamicApi.getPagedRecords('crm', { currentPage: 1, pagesSize: 10 });
    expect(url).toBe('/api/v3/dynamic/modules/crm/records/paged?currentPage=1&pagesSize=10');
  });
});

describe('dynamicApi details and rollup', () => {
  const capture = () => {
    const seen: { method?: string; url?: string; body?: unknown } = {};
    http.defaults.adapter = async (config) => {
      seen.method = config.method;
      seen.url = http.getUri(config);
      seen.body = config.data ? JSON.parse(config.data as string) : undefined;
      return { data: {}, status: 200, statusText: 'OK', headers: {}, config };
    };
    return seen;
  };
  const details = { items: [{ values: { qty: 2 } }, { id: 9, rowVersion: 'rv', _delete: true as const }] };

  it('createRecord sends details', async () => {
    const seen = capture();
    await dynamicApi.createRecord('order', { name: 'A' }, undefined, details);
    expect(seen.method).toBe('post');
    expect(seen.body).toEqual({ values: { name: 'A' }, details });
  });

  it('updateRecord sends details', async () => {
    const seen = capture();
    await dynamicApi.updateRecord(5, { name: 'A' }, 'rv1', details);
    expect(seen.method).toBe('put');
    expect(seen.body).toEqual({ values: { name: 'A' }, rowVersion: 'rv1', details });
  });

  it('record bodies omit details when absent', async () => {
    const seen = capture();
    await dynamicApi.updateRecord(5, {}, 'rv1');
    expect(seen.body).toEqual({ values: {}, rowVersion: 'rv1' });
  });

  it('rollupSources calls the module endpoint', async () => {
    const seen = capture();
    await dynamicApi.rollupSources('order');
    expect(seen.method).toBe('get');
    expect(seen.url).toBe('/api/v3/dynamic/modules/order/rollup-sources');
  });
});

describe('dynamicApi export and import', () => {
  beforeEach(() => setActivePinia(createPinia()));
  const reply = (status: number, data: unknown, headers: Record<string, string> = {}) => {
    const seen: { url: string; method?: string; data?: unknown } = { url: '' };
    http.defaults.adapter = async (config) => {
      Object.assign(seen, { url: http.getUri(config), method: config.method, data: config.data });
      return { data, status, statusText: '', headers, config };
    };
    return seen;
  };

  it('exportRecords sends the query and takes the file name from filename*', async () => {
    const seen = reply(200, new Blob(['x']), { 'content-disposition': "attachment; filename=crm.csv; filename*=UTF-8''%E5%AE%A2%E6%88%B6.csv" });
    const r = await dynamicApi.exportRecords('crm', 'csv', { filter: '{"a":1}', sort: [{ field: 3, order: 'desc' }], q: 'k' }, 'th');
    const p = new URL(seen.url, 'http://x').searchParams;
    expect(seen.url).toContain('/modules/crm/export?');
    expect([p.get('format'), p.get('filter'), p.get('sortBy'), p.get('sortOrder'), p.get('q'), p.get('locale')]).toEqual(['csv', '{"a":1}', 'f_3', 'desc', 'k', 'th']);
    expect(r.fileName).toBe('客戶.csv');
    expect(r.blob).toBeInstanceOf(Blob);
  });

  it('exportRecords falls back to the plain filename, then to key.format', async () => {
    reply(200, new Blob(['x']), { 'content-disposition': 'attachment; filename="crm-1.xlsx"' });
    expect((await dynamicApi.exportRecords('crm', 'xlsx', {}, 'zh-TW')).fileName).toBe('crm-1.xlsx');
    reply(200, new Blob(['x']));
    expect((await dynamicApi.exportRecords('crm', 'xlsx', {}, 'zh-TW')).fileName).toBe('crm.xlsx');
  });

  it('exportRecords parses a Blob error body so the 400 message is kept', async () => {
    reply(400, new Blob([JSON.stringify({ errors: { filter: ['匯出最多 10000 筆，請加篩選條件'] } })]));
    await expect(dynamicApi.exportRecords('crm', 'csv', {}, 'zh-TW')).rejects.toMatchObject({
      status: 400, errors: { filter: ['匯出最多 10000 筆，請加篩選條件'] },
    });
    reply(403, new Blob(['']));
    await expect(dynamicApi.exportRecords('crm', 'csv', {}, 'zh-TW')).rejects.toMatchObject({ status: 403 });
  });

  it('previewImport and commitImport post the file as multipart with commit flag', async () => {
    const file = new File(['a'], 'a.csv');
    let seen = reply(200, { columns: [], total: 0, valid: 0, errors: [] });
    await dynamicApi.previewImport('crm', file);
    expect(seen.url).toContain('/modules/crm/import?commit=false');
    expect(seen.data).toBeInstanceOf(FormData);
    expect((seen.data as FormData).get('file')).toBeInstanceOf(File);
    seen = reply(200, { created: 2 });
    expect(await dynamicApi.commitImport('crm', file)).toEqual({ created: 2 });
    expect(seen.url).toContain('commit=true');
  });
});

describe('dynamicApi templates', () => {
  const capture = () => {
    const seen: { method?: string; url?: string; data?: unknown; ct?: unknown } = {};
    http.defaults.adapter = async (config) => {
      seen.method = config.method;
      seen.url = http.getUri(config);
      seen.data = config.data;
      seen.ct = config.headers.get('Content-Type');
      return { data: [], status: 200, statusText: 'OK', headers: {}, config };
    };
    return seen;
  };

  it('getTemplates / instantiateTemplate', async () => {
    const seen = capture();
    await dynamicApi.getTemplates();
    expect([seen.method, seen.url]).toEqual(['get', '/api/v3/dynamic/templates']);
    await dynamicApi.instantiateTemplate('crm', { modules: [{ code: 'a', key: 'b' }], excludeFields: ['a.x'] });
    expect([seen.method, seen.url]).toEqual(['post', '/api/v3/dynamic/templates/crm/instantiate']);
    expect(JSON.parse(seen.data as string)).toEqual({ modules: [{ code: 'a', key: 'b' }], excludeFields: ['a.x'] });
  });

  it('importTemplate sends the text unchanged as application/json', async () => {
    const seen = capture();
    await dynamicApi.importTemplate('{"key":"k"}');
    expect([seen.method, seen.url, seen.data, seen.ct]).toEqual(['post', '/api/v3/dynamic/templates/import', '{"key":"k"}', 'application/json']);
  });

  it('exportTemplate returns the blob and the Content-Disposition name', async () => {
    http.defaults.adapter = async (config) => ({
      data: new Blob(['{}']), status: 200, statusText: 'OK', config,
      headers: { 'content-disposition': 'attachment; filename="order.template.json"' },
    });
    const r = await dynamicApi.exportTemplate('order');
    expect(r.fileName).toBe('order.template.json');
    expect(r.blob).toBeInstanceOf(Blob);
  });
});
