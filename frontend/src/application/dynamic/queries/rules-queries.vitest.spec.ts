import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { createApp } from 'vue';
import { describe, expect, it, vi } from 'vitest';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { useSaveRulesMutation } from './rules-queries';

describe('useSaveRulesMutation', () => {
  it('invalidates all schemas, since a detail module rule change affects its header schema', async () => {
    vi.spyOn(dynamicApi, 'putRules').mockResolvedValue([]);
    const qc = new QueryClient();
    const spy = vi.spyOn(qc, 'invalidateQueries');
    let mutation!: ReturnType<typeof useSaveRulesMutation>;
    createApp({ setup: () => { mutation = useSaveRulesMutation('line'); return () => null; } })
      .use(VueQueryPlugin, { queryClient: qc }).mount(document.createElement('div'));
    await mutation.mutateAsync([]);
    const keys = spy.mock.calls.map((c) => (c[0] as { queryKey?: unknown[] }).queryKey);
    expect(keys).toContainEqual(['schema']);
    expect(keys).not.toContainEqual(['schema', 'line']);
  });
});
