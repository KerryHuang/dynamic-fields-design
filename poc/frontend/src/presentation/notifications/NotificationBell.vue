<template>
  <q-btn flat dense round icon="notifications" class="q-mr-sm" :aria-label="t('notifications.title')" data-testid="notification-bell">
    <q-badge v-if="unread > 0" color="negative" floating rounded data-testid="notification-badge">{{ unread }}</q-badge>
    <q-menu anchor="bottom right" self="top right" class="notification-menu">
      <q-list>
        <q-item>
          <q-item-section class="text-subtitle2">{{ t('notifications.title') }}</q-item-section>
          <q-item-section side>
            <q-btn
              flat dense no-caps color="primary" :label="t('notifications.readAll')" :disable="unread === 0"
              data-testid="notification-read-all" @click="markAll.mutate()"
            />
          </q-item-section>
        </q-item>
        <q-separator />
        <q-item v-if="!items.length" data-testid="notification-empty">
          <q-item-section class="text-muted">{{ t('notifications.empty') }}</q-item-section>
        </q-item>
        <q-item
          v-for="n in items" :key="n.id" v-close-popup clickable :class="{ 'text-weight-bold': !n.read }"
          :data-testid="`notification-item-${n.id}`" @click="open(n)"
        >
          <q-item-section>
            <q-item-label class="notification-text">{{ label(n) }}</q-item-label>
          </q-item-section>
        </q-item>
      </q-list>
    </q-menu>
  </q-btn>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRouter } from 'vue-router';
import {
  useMarkAllNotificationsRead, useMarkNotificationRead, useNotifications,
} from 'src/application/dynamic/queries/notifications-queries';
import { NOTIFICATION_KINDS, type NotificationItem } from 'src/domain/dynamic/task-types';
import { safeRedirect } from 'src/router/auth-guard';

const { t } = useI18n();
const router = useRouter();
const { data } = useNotifications();
const markRead = useMarkNotificationRead();
const markAll = useMarkAllNotificationsRead();

const items = computed(() => data.value?.items ?? []);
const unread = computed(() => data.value?.unreadCount ?? 0);

/** "Kind label + task title"; a kind this build does not know shows the title alone. */
function label(n: NotificationItem) {
  const known = (NOTIFICATION_KINDS as readonly string[]).includes(n.kind);
  return known ? `${t(`notifications.kind.${n.kind}`)} ${n.title}` : n.title;
}

/** Mark as read first, then follow the link. Only in-app paths are followed (never `//host`, `/\host` or a full URL). */
async function open(n: NotificationItem) {
  const link = n.link;
  try {
    await markRead.mutateAsync(n.id);
  } catch {
    // Still follow the link: failing to mark it read must not make the notification unreachable.
  }
  if (link && safeRedirect(link) === link) void router.push(link);
}
</script>

<style scoped>
.notification-text {
  white-space: normal;
  overflow-wrap: anywhere;
}
.notification-menu {
  max-width: min(360px, 92vw);
}
</style>
