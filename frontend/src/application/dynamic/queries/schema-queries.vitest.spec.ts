import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import { defineComponent, ref } from 'vue';
import { HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import { useRecordScopedSchema } from './schema-queries';

function run(enabled: boolean, reply: unknown) {
  const calls: Call[] = mockHttp(() => reply);
  const on = ref(enabled);
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  let q!: ReturnType<typeof useRecordScopedSchema>;
  mount(defineComponent({ setup() { q = useRecordScopedSchema(ref('po'), ref(9), on); return () => null; } }), { global: { plugins: [[VueQueryPlugin, { queryClient: qc }]] } });
  return { calls, q: () => q, on, qc };
}

describe('useRecordScopedSchema', () => {
  it('asks nothing until enabled, then sends recordId', async () => {
    const r = run(false, { key: 'po' });
    await flushPromises();
    expect(r.calls).toHaveLength(0);
    r.on.value = true;
    await flushPromises();
    expect(r.calls).toHaveLength(1);
    expect(r.calls[0]).toMatchObject({ url: '/modules/po/schema', params: { recordId: 9 } });
    expect(r.qc.getQueryCache().getAll()[0]!.queryKey).toEqual(['schema', 'po', 'record', 9]);
  });

  it('does not retry a 403 even with a retrying client', async () => {
    const calls: Call[] = mockHttp(() => new HttpFail(403));
    const qc = new QueryClient({ defaultOptions: { queries: { retry: 3, retryDelay: 1 } } });
    mount(defineComponent({ setup() { useRecordScopedSchema(ref('po'), ref(9), ref(true)); return () => null; } }), { global: { plugins: [[VueQueryPlugin, { queryClient: qc }]] } });
    await flushPromises();
    await new Promise((r) => setTimeout(r, 30));
    expect(calls).toHaveLength(1);
  });
});
