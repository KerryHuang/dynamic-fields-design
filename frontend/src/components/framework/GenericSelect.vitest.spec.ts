import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'src/i18n/zh-TW.json';
import GenericSelect from './GenericSelect.vue';

const options = [{ value: 'a', label: 'Apple' }, { value: 'b', label: 'Banana' }, { value: 'c', label: 'Blueberry', disable: true }];
const mountSelect = (props: Record<string, unknown> = {}, attach = false) =>
  mount(GenericSelect, {
    props: { modelValue: 'a', label: '水果', options, ...props },
    global: { plugins: [[quasar.Quasar, { components: quasar }], createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } })] },
    ...(attach ? { attachTo: document.body } : {}),
  });
Element.prototype.scrollTo = () => {}; // jsdom 沒有 scrollTo，Quasar 選單會呼叫
const wait = async () => { await new Promise((r) => setTimeout(r, 20)); await flushPromises(); };

describe('GenericSelect 可打字篩選', () => {
  it('q-select 有 use-input，單選另有 fill-input / hide-selected', () => {
    const sel = mountSelect().findComponent(quasar.QSelect);
    expect(sel.props('useInput')).toBe(true);
    expect(sel.props('fillInput')).toBe(true);
    expect(sel.props('hideSelected')).toBe(true);
  });
  it('多選保留 use-chips，不設 fill-input', () => {
    const sel = mountSelect({ multiple: true, modelValue: ['a'] }).findComponent(quasar.QSelect);
    expect(sel.props('useChips')).toBe(true);
    expect(sel.props('fillInput')).toBe(false);
  });
  it('輸入字串後只剩符合的選項（disable 保留）', async () => {
    const w = mountSelect();
    const sel = w.findComponent(quasar.QSelect);
    let updated = false;
    sel.vm.$emit('filter', 'b', (fn: () => void) => { fn(); updated = true; });
    await w.vm.$nextTick();
    expect(updated).toBe(true);
    expect(sel.props('options')).toEqual([options[1], options[2]]);
  });
  it('readonly 時輸入框不可編輯', () => {
    const w = mountSelect({ readonly: true });
    expect(w.get('input').attributes('readonly')).toBeDefined();
  });
  it('實際打字：選單只列出符合項，無符合顯示「沒有符合的選項」，失焦後仍顯示原選中值', async () => {
    const w = mountSelect({}, true);
    const input = w.get('input');
    input.element.focus();
    await input.setValue('ban');
    await wait();
    const items = () => [...document.body.querySelectorAll('.q-item')].map((e) => e.textContent?.trim());
    expect(items()).toEqual(['Banana']);
    await input.setValue('zzz');
    await wait();
    expect(items()).toEqual(['沒有符合的選項']);
    (w.findComponent(quasar.QSelect).vm as unknown as { hidePopup(): void }).hidePopup();
    await wait();
    input.element.blur();
    await wait();
    expect((input.element as HTMLInputElement).value).toBe('Apple');
    expect(w.emitted('update:modelValue')).toBeUndefined();
    w.unmount();
  });
  it('多選：被篩掉的已選值 chip 仍在；挑選後輸入框清空', async () => {
    const w = mountSelect({ multiple: true, modelValue: ['a'] }, true);
    const sel = w.findComponent(quasar.QSelect);
    const input = w.get('input');
    input.element.focus();
    await input.setValue('ban');
    await wait();
    expect(w.find('.q-chip').text()).toContain('Apple');
    (sel.vm as unknown as { toggleOption(o: unknown): void }).toggleOption(options[1]);
    await wait();
    expect(w.emitted('update:modelValue')!.at(-1)![0]).toEqual(['a', 'b']);
    expect((input.element as HTMLInputElement).value).toBe('');
    w.unmount();
  });
});
