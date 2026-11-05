import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it } from 'vitest';
import IconSelect, { MODULE_ICONS } from './IconSelect.vue';

const mountSelect = (modelValue: string) =>
  mount(IconSelect, {
    props: { modelValue, label: '圖示' },
    global: { plugins: [[quasar.Quasar, { components: quasar }]] },
  });

describe('IconSelect', () => {
  it('offers the curated icons, starting with the default, and cannot be cleared', () => {
    const select = mountSelect('apps').getComponent(quasar.QSelect);
    expect(MODULE_ICONS[0]).toBe('apps');
    expect(select.props('options')).toEqual(MODULE_ICONS);
    expect(select.props('clearable')).toBe(false);
  });

  it('previews the selected icon and emits the picked name', async () => {
    const w = mountSelect('build');
    expect(w.findComponent(quasar.QIcon).props('name')).toBe('build');
    w.getComponent(quasar.QSelect).vm.$emit('update:modelValue', 'factory');
    expect(w.emitted('update:modelValue')![0]).toEqual(['factory']);
  });
});
