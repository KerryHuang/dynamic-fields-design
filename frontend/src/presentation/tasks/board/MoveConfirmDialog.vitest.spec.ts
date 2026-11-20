import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeAll, describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import MoveConfirmDialog from './MoveConfirmDialog.vue';

const mountDialog = () => mount(MoveConfirmDialog, {
  attachTo: document.body,
  props: { modelValue: true, title: '取消這個任務？', body: '內文' },
  global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } })] },
});

describe('MoveConfirmDialog', () => {
  // Quasar 第一次安裝時會依視窗寬度重設 Screen；先裝一次，測試才能用 Screen.lt.sm 模擬手機
  beforeAll(() => { mountDialog().unmount(); });
  afterEach(() => { quasar.Screen.lt.sm = false; document.body.innerHTML = ''; });
  const maximized = () => document.body.querySelector('.q-dialog__inner--maximized') !== null;
  const cardStyle = () => document.body.querySelector<HTMLElement>('[data-testid="move-confirm"]')!.getAttribute('style') ?? '';

  it('fills the screen below 600px, and keeps its fixed width above', async () => {
    quasar.Screen.lt.sm = true;
    const phone = mountDialog();
    await flushPromises();
    expect(maximized()).toBe(true);
    expect(cardStyle()).not.toContain('width: 360px');
    phone.unmount();
    document.body.innerHTML = '';
    quasar.Screen.lt.sm = false;
    const desktop = mountDialog();
    await flushPromises();
    expect(maximized()).toBe(false);
    expect(cardStyle()).toContain('width: 360px');
    desktop.unmount();
  });

  it('confirm emits and closes; cancel just closes', async () => {
    const w = mountDialog();
    await flushPromises();
    document.body.querySelector<HTMLElement>('[data-testid="move-confirm-ok"]')!.click();
    await flushPromises();
    expect(w.emitted('confirm')).toHaveLength(1);
    w.unmount();
  });
});
