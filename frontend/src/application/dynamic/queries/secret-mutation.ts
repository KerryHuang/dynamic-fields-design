import { onScopeDispose } from 'vue';

/** 機密 mutation 不離線排隊、不重試：送出當下就開始，不會延到之後才取值。 */
export const SECRET_MUTATION = { networkMode: 'always', retry: false } as const;

/**
 * 機密值（密碼）綁定「當次提交」，不進 mutation variables（MutationCache 保留 variables 與 mutationFn 閉包）：
 * send 時記在只有這個 hook 讀得到的表，mutationFn 一開始就取走並刪除；元件卸載時清空，
 * 之後才開始的請求拋錯中止，不會改送空值或別次輸入的值。
 */
export function useSecretMutation<V extends object, S, M extends { mutate: (v: V, o?: never) => void }>(
  make: (take: (v: V) => S) => M,
) {
  const pending = new Map<V, S>();
  onScopeDispose(() => pending.clear());
  const m = make((v) => {
    if (!pending.has(v)) throw new Error('secret-revoked');
    const s = pending.get(v) as S;
    pending.delete(v);
    return s;
  });
  const mutate = m.mutate.bind(m);
  // 每次 send 另建 variables 快照當 key 與 mutate 參數：呼叫端重用同一物件也不會互相覆蓋或取走別次的密碼。
  // 原始 mutate／mutateAsync 不對外：只能經 send 送出。
  return Object.assign(m, {
    send: (v: V, s: S, o?: Parameters<M['mutate']>[1]) => {
      const snap = { ...v } as V;
      pending.set(snap, s);
      mutate(snap, o as never);
    },
    mutate: undefined as never,
    mutateAsync: undefined as never,
  });
}
