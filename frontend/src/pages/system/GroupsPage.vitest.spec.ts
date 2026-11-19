import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import zhTW from 'src/i18n/zh-TW.json';
import type { AdminGroup, AdminUser, GroupImpact, SystemPrivileges } from 'src/domain/dynamic/types';
import { noSystemPrivileges } from 'src/domain/dynamic/system-privileges';
import { dynamicApi } from 'src/services/api/dynamic-api';
import GroupsPage from './GroupsPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: {
    getAdminGroups: vi.fn(), createGroup: vi.fn(), renameGroup: vi.fn(), setGroupMembers: vi.fn(), getGroupImpact: vi.fn(),
    deleteGroup: vi.fn(), getAdminUsers: vi.fn(), setGroupSystemPrivileges: vi.fn(),
  },
}));
const api = vi.mocked(dynamicApi);

const user = (userId: string, extra: Partial<AdminUser> = {}): AdminUser => ({
  userId, displayName: `名-${userId}`, isActive: true, isSystemAdmin: false, isDemoAccount: false, isLocked: false, groupIds: [],
  systemPrivileges: noSystemPrivileges(), effectiveSystemPrivileges: noSystemPrivileges(), systemPrivilegeSources: [], ...extra,
});
const USERS = [user('a01'), user('b02'), user('old01', { isActive: false })];
const GROUPS: AdminGroup[] = [
  { id: 1, key: 'sales', nameI18n: { 'zh-TW': '業務群組', 'en-US': 'Sales' }, memberUserIds: ['a01', 'old01'], systemPrivileges: noSystemPrivileges() },
  { id: 2, key: 'g-abc', nameI18n: { 'zh-TW': '空群組' }, memberUserIds: [], systemPrivileges: noSystemPrivileges() },
];
const IMPACT: GroupImpact = { memberCount: 2, modules: ['訂單', '客戶'], fieldPermissionCount: 3, dataScopeCount: 1, blockingOptions: [], reports: ['月報', '客戶分析'], dashboards: ['業務儀表板'], systemPrivileges: noSystemPrivileges() };

let mounted: ReturnType<typeof mount> | undefined;

async function mountPage(locale = 'zh-TW', groups: AdminGroup[] = GROUPS, width = 1366) {
  window.innerWidth = width;
  setActivePinia(createPinia());
  api.getAdminGroups.mockResolvedValue(groups);
  api.getAdminUsers.mockResolvedValue(USERS);
  const i18n = createI18n({ legacy: false, locale, fallbackLocale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(GroupsPage))) };
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const w = mount(Host, {
    attachTo: document.body,
    global: {
      plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], i18n,
        [VueQueryPlugin, { queryClient }]],
    },
  });
  await flushPromises();
  // Quasar's Screen listens to resize (debounced); jsdom starts at 1024 wide
  window.dispatchEvent(new Event('resize'));
  await new Promise((r) => setTimeout(r, 150));
  await flushPromises();
  mounted = w;
  return w;
}
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLInputElement | null;
const click = async (id: string) => { q(id)!.click(); await flushPromises(); };
async function fill(id: string, value: string) {
  const el = q(id)!;
  el.value = value;
  el.dispatchEvent(new Event('input'));
  await flushPromises();
}

describe('GroupsPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  afterEach(() => { mounted?.unmount(); document.body.innerHTML = ''; });

  it('lists names resolved by the current locale and member counts', async () => {
    await mountPage('en-US');
    expect(q('group-row-1')!.textContent).toContain('Sales');
    expect(q('group-row-1')!.textContent).toContain('2');
    expect(q('group-row-2')!.textContent).toContain('空群組');
  });

  it('create sends the name with the current locale', async () => {
    api.createGroup.mockResolvedValue(GROUPS[1]);
    await mountPage('en-US');
    await click('group-create');
    await fill('group-name', 'Support');
    await click('group-save');
    expect(api.createGroup).toHaveBeenCalledWith({ name: 'Support', locale: 'en-US' });
  });

  it('click a row opens one dialog with name and members; renaming sends the name with the locale and skips unchanged members', async () => {
    api.renameGroup.mockResolvedValue(GROUPS[0]);
    await mountPage();
    expect(q('group-row-1')!.classList.contains('row-clickable')).toBe(true);
    await click('group-row-1');
    expect(q('group-name')!.value).toBe('業務群組');
    expect(q('member-a01')).not.toBeNull();
    await fill('group-name', '業務部');
    await click('group-save');
    expect(api.renameGroup).toHaveBeenCalledWith(1, { name: '業務部', locale: 'zh-TW' });
    expect(api.setGroupMembers).not.toHaveBeenCalled();
  });

  it('keyboard: the row is focusable and Enter opens the edit dialog', async () => {
    await mountPage();
    expect(q('group-row-1')!.getAttribute('tabindex')).toBe('0');
    q('group-row-1')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }));
    await flushPromises();
    expect(q('group-name')!.value).toBe('業務群組');
  });

  it('members only: renameGroup is not called', async () => {
    api.setGroupMembers.mockResolvedValue(undefined as never);
    await mountPage();
    await click('group-row-1');
    await click('member-b02');
    await click('group-save');
    expect(api.renameGroup).not.toHaveBeenCalled();
    expect(api.setGroupMembers).toHaveBeenCalledTimes(1);
  });

  it('rename failure keeps the dialog open, shows the error and never sets members', async () => {
    api.renameGroup.mockRejectedValue({ status: 400, errors: { name: ['已有同名的群組'] } });
    await mountPage();
    await click('group-row-1');
    await fill('group-name', '空群組');
    await click('member-b02');
    await click('group-save');
    expect(q('group-error')!.textContent).toContain('已有同名的群組');
    expect(q('group-name')).not.toBeNull();
    expect(api.setGroupMembers).not.toHaveBeenCalled();
  });

  it('members failure after a successful rename keeps the dialog open and the retry does not rename again', async () => {
    api.renameGroup.mockResolvedValue(GROUPS[0]);
    api.setGroupMembers.mockRejectedValueOnce({ status: 500 }).mockResolvedValueOnce(undefined as never);
    await mountPage();
    await click('group-row-1');
    await fill('group-name', '業務部');
    await click('member-b02');
    await click('group-save');
    expect(q('group-error')!.textContent).toContain(zhTW.groups.saveFailed);
    expect(q('group-name')).not.toBeNull();
    expect(api.renameGroup).toHaveBeenCalledTimes(1);
    await click('group-save');
    expect(api.renameGroup).toHaveBeenCalledTimes(1);
    expect(api.setGroupMembers).toHaveBeenCalledTimes(2);
    expect(q('group-name')).toBeNull();
  });

  it('the delete button does not open the edit dialog', async () => {
    api.getGroupImpact.mockResolvedValue(IMPACT);
    await mountPage();
    await click('group-delete-1');
    expect(q('group-name')).toBeNull();
    expect(q('group-delete-confirm')).not.toBeNull();
  });

  it('changing name and members in the same dialog saves both', async () => {
    api.renameGroup.mockResolvedValue(GROUPS[0]);
    api.setGroupMembers.mockResolvedValue(undefined as never);
    await mountPage();
    await click('group-row-1');
    await fill('group-name', '業務部');
    await click('member-b02');
    await click('group-save');
    expect(api.renameGroup).toHaveBeenCalledWith(1, { name: '業務部', locale: 'zh-TW' });
    expect(api.setGroupMembers).toHaveBeenCalledWith(1, { userIds: ['a01', 'old01', 'b02'] });
  });

  it('duplicate-name 400 is shown inside the dialog', async () => {
    api.createGroup.mockRejectedValue({ status: 400, errors: { name: ['已有同名的群組'] } });
    await mountPage();
    await click('group-create');
    await fill('group-name', '業務群組');
    await click('group-save');
    expect(q('group-error')!.textContent).toContain('已有同名的群組');
    expect(q('group-save')).not.toBeNull();
  });

  it('member picker offers only active users, keeps inactive existing members, and sends userIds', async () => {
    api.setGroupMembers.mockResolvedValue(undefined as never);
    await mountPage();
    await click('group-row-1');
    expect(q('member-a01')).not.toBeNull();
    expect(q('member-b02')).not.toBeNull();
    expect(q('member-old01')!.textContent).toContain(zhTW.users.inactive);
    await click('member-b02');
    await click('group-save');
    expect(api.setGroupMembers).toHaveBeenCalledWith(1, { userIds: ['a01', 'old01', 'b02'] });
  });

  it('member picker does not list an inactive user who is not in the group', async () => {
    await mountPage();
    await click('group-row-2');
    expect(q('member-a01')).not.toBeNull();
    expect(q('member-old01')).toBeNull();
  });

  it('delete asks impact first and shows counts and the loss warning', async () => {
    api.getGroupImpact.mockResolvedValue(IMPACT);
    await mountPage();
    await click('group-delete-1');
    expect(api.getGroupImpact).toHaveBeenCalledWith(1);
    expect(q('impact-members')!.textContent).toContain('2');
    expect(q('impact-modules')!.textContent).toContain('訂單');
    expect(q('impact-modules')!.textContent).toContain('客戶');
    expect(q('impact-fields')!.textContent).toContain('3');
    expect(q('impact-scopes')!.textContent).toContain('1');
    expect(q('impact-reports')!.textContent).toContain('月報');
    expect(q('impact-reports')!.textContent).toContain('客戶分析');
    expect(q('impact-dashboards')!.textContent).toContain('業務儀表板');
    expect(q('impact-warning')!.textContent).toContain('成員會失去這個群組的授權與限制');
    expect(api.deleteGroup).not.toHaveBeenCalled();
    api.deleteGroup.mockResolvedValue(undefined as never);
    await click('group-delete-confirm');
    expect(api.deleteGroup).toHaveBeenCalledWith(1);
  });

  it('delete dialog says none when no report is shared with the group', async () => {
    api.getGroupImpact.mockResolvedValue({ ...IMPACT, reports: [], dashboards: [] });
    await mountPage();
    await click('group-delete-1');
    expect(q('impact-reports')!.textContent).toContain('無');
    expect(q('impact-dashboards')!.textContent).toContain('無');
  });

  it('blockingOptions are listed and the delete button is disabled', async () => {
    api.getGroupImpact.mockResolvedValue({ ...IMPACT, blockingOptions: [{ module: '訂單', field: '狀態', option: '已結案' }] });
    await mountPage();
    await click('group-delete-1');
    expect(q('impact-blocking')!.textContent).toContain('訂單');
    expect(q('impact-blocking')!.textContent).toContain('狀態');
    expect(q('impact-blocking')!.textContent).toContain('已結案');
    expect(q('group-delete-confirm')!.disabled).toBe(true);
  });

  it('a 409 on delete lists the options and keeps the dialog open', async () => {
    api.getGroupImpact.mockResolvedValue(IMPACT);
    api.deleteGroup.mockRejectedValue({
      status: 409, message: '有欄位選項範圍還指定這個群組', blockingOptions: [{ module: '客戶', field: '等級', option: 'VIP' }],
    });
    await mountPage();
    await click('group-delete-1');
    await click('group-delete-confirm');
    expect(q('impact-blocking')!.textContent).toContain('VIP');
    expect(q('group-delete-confirm')!.disabled).toBe(true);
  });

  describe('系統權限（權限軸-b）', () => {
    const sp = (designer: Partial<SystemPrivileges['designer']>, permissionAdmin = false): SystemPrivileges =>
      ({ designer: { ...noSystemPrivileges().designer, ...designer }, permissionAdmin });
    const IT: AdminGroup = { id: 3, key: 'it', nameI18n: { 'zh-TW': 'IT' }, memberUserIds: ['a01'], systemPrivileges: sp({ read: true, edit: true }, true) };
    const WITH_IT = [...GROUPS, IT];
    const checked = (id: string) => q(id)!.getAttribute('aria-checked');

    it('PAX-32 編輯對話框載入直接設定，改完存檔送 PUT admin/groups/{id}/system-privileges（沒有繼承行）', async () => {
      api.setGroupSystemPrivileges.mockResolvedValue(sp({ read: true, edit: true }, false));
      await mountPage('zh-TW', WITH_IT);
      await click('group-row-3');
      expect(checked('sp-designer-read')).toBe('true');
      expect(checked('sp-designer-add')).toBe('false');
      expect(checked('sp-designer-edit')).toBe('true');
      expect(checked('sp-permission-admin')).toBe('true');
      expect(q('system-privileges')!.textContent).toContain('權限設定可以決定誰看得到、改得到資料，但不能授予這兩個系統權限');
      expect(q('system-privileges-inherited')).toBeNull();
      await click('sp-permission-admin');
      await click('group-save');
      expect(api.renameGroup).not.toHaveBeenCalled();
      expect(api.setGroupMembers).not.toHaveBeenCalled();
      expect(api.setGroupSystemPrivileges).toHaveBeenCalledWith(3, sp({ read: true, edit: true }, false));
    });

    it('PAX-32 沒改系統權限就不送；新增群組沒有系統權限區塊', async () => {
      api.renameGroup.mockResolvedValue(IT);
      await mountPage('zh-TW', WITH_IT);
      await click('group-row-3');
      await fill('group-name', 'IT 部');
      await click('group-save');
      expect(api.renameGroup).toHaveBeenCalled();
      expect(api.setGroupSystemPrivileges).not.toHaveBeenCalled();
      await click('group-create');
      expect(q('system-privileges')).toBeNull();
    });

    it('PAX-32 系統權限失敗：名稱與成員已存、對話框保持開啟並說明；重試只送系統權限', async () => {
      api.renameGroup.mockResolvedValue(IT);
      api.setGroupSystemPrivileges.mockRejectedValueOnce({ status: 403, message: '沒有權限執行此操作' });
      await mountPage('zh-TW', WITH_IT);
      await click('group-row-3');
      await fill('group-name', 'IT 部');
      await click('sp-designer-add');
      await click('group-save');
      expect(q('group-error')!.textContent).toBe('名稱與成員已儲存，系統權限未儲存：沒有權限執行此操作');
      api.setGroupSystemPrivileges.mockResolvedValueOnce(sp({ read: true, add: true, edit: true }, true));
      await click('group-save');
      expect(api.renameGroup).toHaveBeenCalledTimes(1);
      expect(api.setGroupSystemPrivileges).toHaveBeenCalledTimes(2);
      expect(api.setGroupSystemPrivileges).toHaveBeenLastCalledWith(3, sp({ read: true, add: true, edit: true }, true));
    });

    it('PAX-32 刪除對話框在群組持有系統權限時列出會移除的系統權限；沒有就不列', async () => {
      api.getGroupImpact.mockResolvedValue({ ...IMPACT, systemPrivileges: sp({ read: true, edit: true }, true) });
      await mountPage('zh-TW', WITH_IT);
      await click('group-delete-3');
      expect(q('impact-system-privileges')!.textContent!.trim())
        .toBe('此群組提供的系統權限會移除：設計器（讀取、修改）、權限設定；成員若從其他群組或個人設定取得，仍會保留');
      await click('group-delete-cancel');
      api.getGroupImpact.mockResolvedValue(IMPACT);
      await click('group-delete-1');
      expect(q('impact-members')).not.toBeNull();
      expect(q('impact-system-privileges')).toBeNull();
    });

    it('PAX-33 群組清單以直接設定顯示「設計器」「權限設定」標籤（桌機表格）', async () => {
      const designerOnly: AdminGroup = { ...GROUPS[1]!, systemPrivileges: sp({ read: true }) };
      await mountPage('zh-TW', [GROUPS[0]!, designerOnly, IT]);
      expect(q('group-sp-3')!.textContent).toContain('設計器');
      expect(q('group-sp-3')!.textContent).toContain('權限設定');
      expect(q('group-sp-2')!.textContent).toContain('設計器');
      expect(q('group-sp-2')!.textContent).not.toContain('權限設定');
      expect(q('group-sp-1')!.textContent!.trim()).toBe('');
    });

    it('PAX-33 390 寬改用卡片，卡片同樣顯示標籤；點卡片開編輯、刪除鈕不開', async () => {
      await mountPage('zh-TW', WITH_IT, 390);
      expect(document.body.querySelector('[data-testid^="group-row-"]')).toBeNull();
      expect(q('group-card-3')!.textContent).toContain('IT');
      expect(q('group-sp-3')!.textContent).toContain('權限設定');
      api.getGroupImpact.mockResolvedValue(IMPACT);
      await click('group-delete-3');
      expect(q('group-name')).toBeNull();
      await click('group-delete-cancel');
      await click('group-card-3');
      expect(q('group-name')!.value).toBe('IT');
    });
  });
});
