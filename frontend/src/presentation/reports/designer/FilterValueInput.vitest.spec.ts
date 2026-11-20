import { VueQueryPlugin, QueryClient } from '@tanstack/vue-query';
import { mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, describe, expect, it } from 'vitest';
import { i18n } from 'src/boot/i18n';
import { setBusinessOffset } from 'src/domain/dynamic/business-time';
import type { CatalogField } from 'src/domain/dynamic/report-types';
import FilterValueInput from './FilterValueInput.vue';

afterEach(() => setBusinessOffset('+08:00'));

describe('FilterValueInput', () => {
  it('converts other offsets', () => {
    setBusinessOffset('+07:00');
    const field = { id: 1, type: 'datetime', options: [] } as unknown as CatalogField;
    const w = mount(FilterValueInput, {
      props: { field, op: 'gt', modelValue: '2026-11-11T10:00:00+08:00' },
      global: { plugins: [[quasar.Quasar, { components: quasar }], i18n, createPinia(), [VueQueryPlugin, { queryClient: new QueryClient() }]] },
    });
    expect((w.find('input').element as HTMLInputElement).value).toBe('2026-11-11T09:00');
  });
});
