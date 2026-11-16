import { computed, ref, toValue, type MaybeRefOrGetter } from 'vue';

const defaultLabel = (o: unknown): string =>
  typeof o === 'string' ? o : String((o as { label?: unknown } | null)?.label ?? '');

/** q-select 的打字篩選：label 包含輸入字串（不分大小寫、去前後空白）；空字串顯示全部。 */
export function useSelectFilter<T>(options: MaybeRefOrGetter<T[]>, label: (o: T) => string = defaultLabel) {
  const needle = ref('');
  /** 以目前輸入字串篩任意選項清單（同一個 q-select 在迴圈裡、每格選項不同時用）。 */
  const filterBy = (all: T[]) => (needle.value ? all.filter((o) => label(o).toLowerCase().includes(needle.value)) : all);
  const filtered = computed(() => filterBy(toValue(options)));
  function onFilter(val: string, update: (fn: () => void) => void) {
    update(() => { needle.value = val.trim().toLowerCase(); });
  }
  /** 選單收起時呼叫，清掉篩選字串，避免下次掛載／開啟帶著舊字串。 */
  const reset = () => { needle.value = ''; };
  return { filtered, filterBy, onFilter, reset };
}
