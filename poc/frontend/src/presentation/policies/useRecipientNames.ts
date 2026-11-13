import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { useGroups, useUsers } from 'src/application/dynamic/queries/permissions-queries';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { Group, User } from 'src/domain/dynamic/types';

export interface RecipientOption { value: string; label: string }

/** 選項順序：全部系統管理員、群組、使用者（`useUsers` 只含啟用中的帳號）。 */
export function buildRecipientOptions(
  users: User[], groups: Group[], adminsLabel: string, locale: string,
): RecipientOption[] {
  return [
    { value: 'admins', label: adminsLabel },
    ...groups.map((g) => ({ value: `group:${g.id}`, label: resolveI18n(g.name, locale) })),
    ...users.map((u) => ({ value: `user:${u.userId}`, label: u.displayName || u.userId })),
  ];
}

/** 政策頁與模組政策分頁共用：收件人項目對應的顯示名稱與可選項目。 */
export function useRecipientNames() {
  const { t, locale } = useI18n();
  const users = useUsers();
  const groups = useGroups();
  const options = computed(() => buildRecipientOptions(users.data.value ?? [], groups.data.value ?? [], t('policies.recipients.admins'), locale.value));
  const names = computed(() => new Map(options.value.map((o) => [o.value, o.label])));
  return { options, names };
}
