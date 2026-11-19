import { QueryClient, VueQueryPlugin, useMutation } from '@tanstack/vue-query';
import { createApp } from 'vue';
import { describe, expect, it } from 'vitest';
import { SECRET_MUTATION, useSecretMutation } from './secret-mutation';

function mount(sent: Array<{ id: string; pw: string }>) {
  const qc = new QueryClient();
  let m!: ReturnType<typeof make>;
  function make() {
    return useSecretMutation((take: (v: { id: string }) => string) =>
      useMutation({
        ...SECRET_MUTATION,
        mutationFn: async (v: { id: string }) => { sent.push({ id: v.id, pw: take(v) }); },
      }));
  }
  const app = createApp({ setup: () => { m = make(); return () => null; } }).use(VueQueryPlugin, { queryClient: qc });
  app.mount(document.createElement('div'));
  return { m, qc, app };
}

describe('useSecretMutation（短核對：提交隔離）', () => {
  it('同一 variables 物件連續送兩次，各自送出當次的密碼', async () => {
    const sent: Array<{ id: string; pw: string }> = [];
    const { m, qc } = mount(sent);
    const v = { id: 'A' };
    m.send(v, 'pw-1');
    m.send(v, 'pw-2');
    await new Promise((r) => setTimeout(r, 0));
    expect(sent).toEqual([{ id: 'A', pw: 'pw-1' }, { id: 'A', pw: 'pw-2' }]);
    expect(JSON.stringify(qc.getMutationCache().getAll().map((x) => x.state))).not.toMatch(/pw-/);
  });

  it('原始 mutate／mutateAsync 不對外，只能經 send 送出', () => {
    const { m } = mount([]);
    expect(m.mutate).toBeUndefined();
    expect(m.mutateAsync).toBeUndefined();
  });
});
