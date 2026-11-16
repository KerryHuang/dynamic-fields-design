import { computed, toValue, type MaybeRefOrGetter } from 'vue';
import { useI18n } from 'vue-i18n';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { Group, User } from 'src/domain/dynamic/types';
import { useGroups, useUsers } from 'src/application/dynamic/queries/permissions-queries';

export interface RecipientOption { value: string; label: string }

/** 選項順序：全部系統管理員、群組、使用者（`/users` 只含啟用中的帳號）；使用者標籤含帳號，同名也分得出來、可用帳號搜尋。 */
export function buildRecipientOptions(
  users: User[], groups: Group[], adminsLabel: string, locale: string,
  userLabel: (name: string, id: string) => string,
): RecipientOption[] {
  return [
    { value: 'admins', label: adminsLabel },
    ...groups.map((g) => ({ value: `group:${g.id}`, label: resolveI18n(g.name, locale) })),
    ...users.map((u) => ({ value: `user:${u.userId}`, label: userLabel(u.displayName || u.userId, u.userId) })),
  ];
}

/**
 * 政策頁與模組政策分頁共用。`enabled` 為 false 時不送請求（非系統管理員進政策頁）；
 * 使用者與群組都載入成功之前 `names` 是 undefined，畫面顯示原始項目，不誤報「找不到或已停用」。
 */
export function useRecipientNames(enabled: MaybeRefOrGetter<boolean> = true) {
  const { t, locale } = useI18n();
  const on = computed(() => toValue(enabled));
  const users = useUsers({ enabled: on });
  const groups = useGroups({ enabled: on });
  const options = computed(() => buildRecipientOptions(
    users.data.value ?? [], groups.data.value ?? [], t('policies.recipients.admins'), locale.value,
    (name, id) => t('policies.recipients.userLabel', { name, id }),
  ));
  const ready = computed(() => users.isSuccess.value && groups.isSuccess.value);
  const names = computed(() => (ready.value ? new Map(options.value.map((o) => [o.value, o.label])) : undefined));
  return { options, names, ready };
}
