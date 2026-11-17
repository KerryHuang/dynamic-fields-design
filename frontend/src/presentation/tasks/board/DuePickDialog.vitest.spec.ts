import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'src/i18n/zh-TW.json';
import DuePickDialog from './DuePickDialog.vue';

function mountDialog() {
  return mount(DuePickDialog, {
    attachTo: document.body,
    props: { modelValue: true, weekEnd: '2026-11-22', defaultDate: '2026-11-29' },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } })] },
  });
}
const ok = () => document.body.querySelector<HTMLButtonElement>('[data-testid="due-pick-ok"]')!;

describe('DuePickDialog', () => {
  afterEach(() => { document.body.innerHTML = ''; });

  it('DuePickDialog defaults to laterDefault and rejects dates up to weekEnd', async () => {
    const w = mountDialog();
    await flushPromises();
    const date = w.findComponent(quasar.QDate);
    expect(date.props('modelValue')).toBe('2026-11-29');
    expect(document.body.textContent).toContain('只能選 2026-11-22 之後的日期');
    // 可選日期的判斷：週末當天與之前不行
    const options = date.props('options') as (d: string) => boolean;
    expect(options('2026/11/22')).toBe(false);
    expect(options('2026/11/10')).toBe(false);
    expect(options('2026/11/23')).toBe(true);
    // 輸入到不合法的日期：確定鍵停用並說明
    date.vm.$emit('update:modelValue', '2026-11-22');
    await flushPromises();
    expect(ok().disabled).toBe(true);
    expect(document.body.querySelector('[data-testid="due-pick-invalid"]')!.textContent).toBe('日期必須晚於 2026-11-22');
    ok().click();
    expect(w.emitted('confirm')).toBeUndefined();
    date.vm.$emit('update:modelValue', '2026-12-03');
    await flushPromises();
    expect(ok().disabled).toBe(false);
    ok().click();
    await flushPromises();
    expect(w.emitted('confirm')).toEqual([['2026-12-03']]);
    expect(w.emitted('update:modelValue')?.at(-1)).toEqual([false]);
    w.unmount();
  });

  it('confirms the default date as is, and cancel closes without confirming', async () => {
    const w = mountDialog();
    await flushPromises();
    ok().click();
    await flushPromises();
    expect(w.emitted('confirm')).toEqual([['2026-11-29']]);
    w.unmount();
    const c = mountDialog();
    await flushPromises();
    document.body.querySelector<HTMLButtonElement>('[data-testid="due-pick-cancel"]')!.click();
    await flushPromises();
    expect(c.emitted('confirm')).toBeUndefined();
    c.unmount();
  });

  it('opens again on the default date after an earlier pick', async () => {
    const w = mountDialog();
    await flushPromises();
    w.findComponent(quasar.QDate).vm.$emit('update:modelValue', '2026-12-10');
    await w.setProps({ modelValue: false });
    await w.setProps({ modelValue: true });
    await flushPromises();
    expect(w.findComponent(quasar.QDate).props('modelValue')).toBe('2026-11-29');
    w.unmount();
  });
});
