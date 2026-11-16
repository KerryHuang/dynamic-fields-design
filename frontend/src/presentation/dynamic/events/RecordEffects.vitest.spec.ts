import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it, vi } from 'vitest';
import { ref } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import type { RecordEffects as Effects } from 'src/domain/dynamic/events-ledger';
import enUS from 'src/i18n/en-US.json';
import zhTW from 'src/i18n/zh-TW.json';
import RecordEffects from './RecordEffects.vue';

const data = ref<Effects>({
  items: [{
    eventKey: 'ship-post', effectCode: 'post', kind: 'create', targetModuleKey: 'stock-move',
    targetModuleName: { 'zh-TW': '庫存異動', 'en-US': 'Stock movement' }, targetRecordId: 31, title: '出貨 A', createdTime: '2026-11-08T09:30:00',
  }],
  hiddenCount: 0,
});
vi.mock('src/application/dynamic/queries/ledger-queries', () => ({ useRecordEffects: () => ({ data }) }));

function mountFor(locale: 'zh-TW' | 'en-US') {
  const i18n = createI18n({ legacy: false, locale, fallbackLocale: 'zh-TW', messages: { 'zh-TW': zhTW, 'en-US': enUS } });
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:p(.*)', component: { render: () => null } }] });
  return mount(RecordEffects, { props: { recordId: 5 }, global: { plugins: [i18n, router, quasar.Quasar] } });
}

describe('RecordEffects 目標模組名稱', () => {
  it('依介面語系挑名稱，缺的語系退回繁中', () => {
    expect(mountFor('en-US').find('.effect-caption').text()).toMatch(/^Stock movement · /);
    expect(mountFor('zh-TW').find('.effect-caption').text()).toMatch(/^庫存異動 · /);
  });
});
