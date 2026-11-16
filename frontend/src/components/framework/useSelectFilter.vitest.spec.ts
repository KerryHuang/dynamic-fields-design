import { ref } from 'vue';
import { describe, expect, it } from 'vitest';
import { useSelectFilter } from './useSelectFilter';

const opts = [{ label: 'xABy' }, { label: 'cd' }, { label: 'ab2' }];
const typed = (f: { onFilter: (v: string, u: (fn: () => void) => void) => void }, v: string) => f.onFilter(v, (fn) => fn());

describe('useSelectFilter', () => {
  it('空字串回全部', () => {
    const f = useSelectFilter(opts);
    typed(f, '');
    expect(f.filtered.value).toHaveLength(3);
  });
  it('不分大小寫、去前後空白做包含比對', () => {
    const f = useSelectFilter(opts);
    typed(f, '  ab ');
    expect(f.filtered.value.map((o) => o.label)).toEqual(['xABy', 'ab2']);
  });
  it('無符合回空陣列', () => {
    const f = useSelectFilter(opts);
    typed(f, 'zzz');
    expect(f.filtered.value).toEqual([]);
  });
  it('字串選項取本身，可自訂 label', () => {
    const s = useSelectFilter(['Apple', 'Banana']);
    typed(s, 'nan');
    expect(s.filtered.value).toEqual(['Banana']);
    const c = useSelectFilter([{ n: 'Foo' }, { n: 'Bar' }], (o) => o.n);
    typed(c, 'ba');
    expect(c.filtered.value).toEqual([{ n: 'Bar' }]);
  });
  it('options 是 ref 時改變後 filtered 跟著更新', () => {
    const o = ref([{ label: 'a1' }]);
    const f = useSelectFilter(o);
    typed(f, 'a');
    expect(f.filtered.value).toHaveLength(1);
    o.value = [{ label: 'a1' }, { label: 'a2' }, { label: 'b' }];
    expect(f.filtered.value).toHaveLength(2);
  });
  it('reset 清掉篩選字串', () => {
    const f = useSelectFilter(opts);
    typed(f, 'zzz');
    f.reset();
    expect(f.filtered.value).toHaveLength(3);
  });
});
