import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { createApp } from 'vue';
import { describe, expect, it, vi } from 'vitest';
import { useDesignerMutation } from './modules-queries';

describe('useDesignerMutation', () => {
  it('invalidates all schemas, since a detail module change affects its header schema', async () => {
    const qc = new QueryClient();
    const spy = vi.spyOn(qc, 'invalidateQueries');
    let mutation!: ReturnType<typeof useDesignerMutation<string, void>>;
    createApp({ setup: () => { mutation = useDesignerMutation(async () => undefined, (k: string) => k); return () => null; } })
      .use(VueQueryPlugin, { queryClient: qc }).mount(document.createElement('div'));
    await mutation.mutateAsync('line');
    const keys = spy.mock.calls.map((c) => (c[0] as { queryKey?: unknown[] }).queryKey);
    expect(keys).toContainEqual(['schema']);
    expect(keys).not.toContainEqual(['schema', 'line']);
    expect(keys).toContainEqual(['module', 'line']);
  });
});
