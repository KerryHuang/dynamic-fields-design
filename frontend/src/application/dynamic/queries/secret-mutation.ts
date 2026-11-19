import { onScopeDispose } from 'vue';

/** 機密 mutation 不離線排隊、不重試：送出當下就開始，不會延到之後才取值。 */
export const SECRET_MUTATION = { networkMode: 'always', retry: false } as const;

/**
 * 機密值（密碼）綁定「當次提交」，不進 mutation variables（MutationCache 保留 variables 與 mutationFn 閉包）：
 * send 時記在只有這個 hook 讀得到的表，mutationFn 一開始就取走並刪除；元件卸載時清空，
 * 之後才開始的請求拋錯中止，不會改送空值或別次輸入的值。variables 必須是每次送出新建的物件。
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
  return Object.assign(m, {
    send: (v: V, s: S, o?: Parameters<M['mutate']>[1]) => {
      pending.set(v, s);
      m.mutate(v, o as never);
    },
  });
}
