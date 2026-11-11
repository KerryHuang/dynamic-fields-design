import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'src/i18n/zh-TW.json';
import type { PolicyMeta, PolicyValue } from 'src/domain/dynamic/policy-types';
import PolicyValueEditor from './PolicyValueEditor.vue';

let w: ReturnType<typeof mount> | undefined;
function make(meta: PolicyMeta, modelValue: PolicyValue) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': { ...zhTW, policies: { ...zhTW.policies, choices: { assignee: '負責人', creator: '建立者' } } } } });
  w = mount(PolicyValueEditor, {
    attachTo: document.body,
    props: { meta, modelValue, label: '測試政策' },
    global: { plugins: [[quasar.Quasar, { components: quasar }], i18n] },
  });
  return w;
}
const lastEmit = () => w!.emitted('update:modelValue')!.at(-1)![0];
afterEach(() => { w?.unmount(); document.body.innerHTML = ''; });

describe('PolicyValueEditor', () => {
  it('Integer：輸入 "7" 送出 number 7，輸入框有可讀名稱', async () => {
    make({ code: 'a', category: 'task', kind: 'integer', min: 1, max: 100 }, 20);
    const input = document.body.querySelector('input') as HTMLInputElement;
    expect(input.getAttribute('aria-label') ?? document.body.querySelector('label')?.textContent).toContain('測試政策');
    input.value = '7';
    input.dispatchEvent(new Event('input'));
    await flushPromises();
    expect(lastEmit()).toBe(7);
  });

  it('Boolean：切換送出 boolean', async () => {
    make({ code: 'a', category: 'task', kind: 'boolean' }, true);
    const toggle = document.body.querySelector('[role="switch"]') as HTMLElement;
    expect(toggle.getAttribute('aria-label') ?? toggle.textContent).toContain('測試政策');
    toggle.click();
    await flushPromises();
    expect(lastEmit()).toBe(false);
  });

  it('Choice：選項文字取自 policies.choices', async () => {
    const m = make({ code: 'a', category: 'x', kind: 'choice', options: ['assignee', 'creator'] }, 'assignee');
    expect(m.text()).toContain('負責人');
  });

  it('RoleSet：勾選送出字串陣列，群組有名稱', async () => {
    make({ code: 'a', category: 'task', kind: 'roleSet', options: ['assignee', 'creator'] }, ['creator']);
    expect(document.body.querySelector('[role="group"], [role="checkbox"]')).toBeTruthy();
    expect(document.body.textContent).toContain('測試政策');
    const boxes = [...document.body.querySelectorAll('[role="checkbox"]')] as HTMLElement[];
    expect(boxes).toHaveLength(2);
    boxes[0].click();
    await flushPromises();
    expect(lastEmit()).toEqual(['assignee', 'creator']);
  });

  it('CodeList：輸入 pdf 按 Enter 送出 [pdf]', async () => {
    make({ code: 'a', category: 'attachment', kind: 'codeList', min: 1, max: 40 }, []);
    const input = document.body.querySelector('input') as HTMLInputElement;
    expect(input.getAttribute('aria-label') ?? document.body.querySelector('label')?.textContent).toContain('測試政策');
    input.focus();
    input.value = 'pdf';
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', keyCode: 13, bubbles: true }));
    await flushPromises();
    expect(lastEmit()).toEqual(['pdf']);
  });
});
