import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, describe, expect, it } from 'vitest';
import { setBusinessOffset } from 'src/domain/dynamic/business-time';
import DateSelect from './DateSelect.vue';

afterEach(() => setBusinessOffset('+08:00'));

describe('DateSelect', () => {
  it('emits business offset', async () => {
    setBusinessOffset('+07:00');
    const w = mount(DateSelect, { props: { modelValue: '2026-11-11T10:00:00+08:00', label: '時間', withTime: true }, global: { plugins: [[quasar.Quasar, { components: quasar }]] } });
    const input = w.find('input');
    expect((input.element as HTMLInputElement).value).toBe('2026-11-11T09:00');
    await input.setValue('2026-11-11T09:30');
    expect(w.emitted('update:modelValue')!.at(-1)).toEqual(['2026-11-11T09:30:00+07:00']);
  });
});
