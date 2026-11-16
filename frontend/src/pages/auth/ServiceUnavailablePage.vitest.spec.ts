import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import zhTW from 'src/i18n/zh-TW.json';
import ServiceUnavailablePage from './ServiceUnavailablePage.vue';

async function retryFrom(redirect: string) {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/service-unavailable', component: ServiceUnavailablePage }, { path: '/:p(.*)*', component: { template: '<i/>' } }],
  });
  await router.push({ path: '/service-unavailable', query: { redirect } });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(ServiceUnavailablePage, { global: { plugins: [[quasar.Quasar, { components: quasar }], i18n, router] } });
  await flushPromises();
  expect(w.text()).toContain(zhTW.auth.serviceUnavailable);
  await w.find('[data-testid="retry"]').trigger('click');
  await flushPromises();
  return router.currentRoute.value.fullPath;
}

describe('ServiceUnavailablePage', () => {
  it('retry goes to the redirect target', async () => {
    expect(await retryFrom('/dynamic/order?q=1')).toBe('/dynamic/order?q=1');
  });

  it('an external redirect goes to /', async () => {
    expect(await retryFrom('//evil.example')).toBe('/');
  });
});
