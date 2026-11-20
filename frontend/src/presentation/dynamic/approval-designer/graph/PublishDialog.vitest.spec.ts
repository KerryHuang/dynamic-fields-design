import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import PublishDialog from './PublishDialog.vue';
import type { PublishInfo } from './use-designer-actions';

const body = (id: string) => document.body.querySelector(`[data-testid="${id}"]`);
function show(info: PublishInfo | null) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(PublishDialog, { props: { info, busy: false }, attachTo: document.body, global: { plugins: [[quasar.Quasar, { components: quasar }], i18n] } });
}
beforeEach(() => { document.body.innerHTML = ''; });

describe('PublishDialog', () => {
  it('with neither warnings nor a new version it is a one-line confirmation', async () => {
    const w = show({ warnings: [], newVersion: false, pendingCount: 0 });
    await new Promise((r) => setTimeout(r, 30));
    expect(body('publish-ask')?.textContent).toBe('確認發布這條流程？');
    expect(body('publish-warnings')).toBeNull();
    expect(body('publish-version')).toBeNull();
    w.unmount();
  });

  it('the version notice shows only when validate says newVersion, with the pending count', async () => {
    const w = show({ warnings: [], newVersion: true, nextVersion: 4, pendingCount: 5 });
    await new Promise((r) => setTimeout(r, 30));
    expect(body('publish-version')?.textContent).toBe('發布後產生第 4 版，進行中的 5 張單照舊版走完');
    await w.setProps({ info: { warnings: [], newVersion: true, nextVersion: 4, pendingCount: 0 } });
    expect(body('publish-version')?.textContent).toBe('發布後產生第 4 版，已送出的單不受影響');
    await w.setProps({ info: { warnings: [], newVersion: false, nextVersion: 4, pendingCount: 5 } });
    expect(body('publish-version')).toBeNull();
    w.unmount();
  });

  it('a new version without a next version number falls back to the one-line confirmation (never an empty body)', async () => {
    const w = show({ warnings: [], newVersion: true, nextVersion: null, pendingCount: 2 });
    await new Promise((r) => setTimeout(r, 30));
    expect(body('publish-version')).toBeNull();
    expect(body('publish-ask')?.textContent).toBe('確認發布這條流程？');
    w.unmount();
  });

  it('warnings are listed and the confirm button says publish anyway; confirm and cancel emit', async () => {
    const w = show({ warnings: [{ key: 'definition.nodes[1]', code: 'W1', message: '可能略過' }], newVersion: false, pendingCount: 0 });
    await new Promise((r) => setTimeout(r, 30));
    expect(body('publish-warnings')?.textContent).toContain('可能略過');
    expect(body('publish-confirm')?.textContent).toContain('仍要發布');
    (body('publish-confirm') as HTMLElement).click();
    (body('publish-cancel') as HTMLElement).click();
    expect(w.emitted('confirm')).toHaveLength(1);
    expect(w.emitted('cancel')).toHaveLength(1);
    w.unmount();
  });
});
