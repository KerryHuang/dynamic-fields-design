import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'src/i18n/zh-TW.json';
import type { PolicyMeta, PolicyValue } from 'src/domain/dynamic/policy-types';
import PolicyValueEditor from './PolicyValueEditor.vue';
import { buildRecipientOptions } from './useRecipientNames';

let w: ReturnType<typeof mount> | undefined;
function make(meta: PolicyMeta, modelValue: PolicyValue, recipientOptions?: { value: string; label: string }[], recipientsReady = false) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': { ...zhTW, policies: { ...zhTW.policies, choices: { assignee: '負責人', creator: '建立者' } } } } });
  w = mount(PolicyValueEditor, {
    attachTo: document.body,
    props: { meta, modelValue, label: '測試政策', recipientOptions, recipientsReady },
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

  it('Integer：清空輸入框送出 update:valid false，重新輸入恢復 true', async () => {
    make({ code: 'a', category: 'task', kind: 'integer', min: 1, max: 100 }, 20);
    const input = document.body.querySelector('input') as HTMLInputElement;
    input.value = '';
    input.dispatchEvent(new Event('input'));
    await flushPromises();
    expect(w!.emitted('update:valid')!.at(-1)![0]).toBe(false);
    input.value = '8';
    input.dispatchEvent(new Event('input'));
    await flushPromises();
    expect(w!.emitted('update:valid')!.at(-1)![0]).toBe(true);
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

  describe('Recipients', () => {
    const meta: PolicyMeta = { code: 'a', category: 'approval', kind: 'recipients', min: 1, max: 20 };
    const options = buildRecipientOptions(
      [{ userId: 'fin1', displayName: '財務一' }, { userId: 'fin2', displayName: '財務二' }],
      [{ id: 7, key: 'finance', name: { 'zh-TW': '財務群組' }, members: [] }, { id: 8, key: 'board', name: { 'zh-TW': '董事會' }, members: [] }],
      '全部系統管理員', 'zh-TW', (name, id) => `${name}（${id}）`,
    );

    it('選項依序是全部系統管理員、群組、使用者，共 5 個', () => {
      expect(options.map((o) => o.value)).toEqual(['admins', 'group:7', 'group:8', 'user:fin1', 'user:fin2']);
      expect(options[0].label).toBe('全部系統管理員');
      expect(options[1].label).toBe('財務群組');
    });

    it('editor shows raw items, not missing, before names are ready', () => {
      make(meta, ['group:7', 'user:gone'], [], false);
      expect(document.body.textContent).toContain('group:7');
      expect(document.body.textContent).toContain('user:gone');
      expect(document.body.textContent).not.toContain('找不到或已停用');
    });

    it('editor shows missing after ready for an unknown item', () => {
      make(meta, ['user:gone'], options, true);
      expect(document.body.textContent).toContain('找不到或已停用');
      expect(document.body.textContent).toContain('user:gone');
    });

    it('已選項目顯示名稱，輸入框有可讀名稱', () => {
      make(meta, ['admins', 'group:7'], options);
      expect(document.body.textContent).toContain('全部系統管理員');
      expect(document.body.textContent).toContain('財務群組');
      const input = document.body.querySelector('input') as HTMLInputElement;
      expect(input.getAttribute('aria-label') ?? document.body.querySelector('label')?.textContent).toContain('測試政策');
    });

    it('移除一個 chip 送出剩下的標準項目', async () => {
      make(meta, ['admins', 'group:7'], options);
      const removes = [...document.body.querySelectorAll('.q-chip__icon--remove')] as HTMLElement[];
      expect(removes).toHaveLength(2);
      removes[0].click();
      await flushPromises();
      expect(lastEmit()).toEqual(['group:7']);
    });

    it('項目數為 0 或超過 20 時送 update:valid false，回到範圍內送 true', async () => {
      make(meta, ['admins'], options);
      const remove = document.body.querySelector('.q-chip__icon--remove') as HTMLElement;
      remove.click();
      await flushPromises();
      expect(w!.emitted('update:valid')!.at(-1)![0]).toBe(false);
      await w!.setProps({ modelValue: Array.from({ length: 21 }, (_, i) => `group:${i + 1}`) });
      expect(w!.emitted('update:valid')!.at(-1)![0]).toBe(false);
      await w!.setProps({ modelValue: ['admins'] });
      expect(w!.emitted('update:valid')!.at(-1)![0]).toBe(true);
    });

    it('使用者選項標籤含帳號，可用帳號搜尋', () => {
      const withAccount = buildRecipientOptions([{ userId: 'fin1', displayName: '財務一' }], [], '全部系統管理員', 'zh-TW', (name, id) => `${name}（${id}）`);
      expect(withAccount[1].label).toBe('財務一（fin1）');
    });

    it('選項裡找不到的已選項目顯示「找不到或已停用」', () => {
      make(meta, ['user:gone'], options, true);
      expect(document.body.textContent).toContain('找不到或已停用');
    });
  });
});
