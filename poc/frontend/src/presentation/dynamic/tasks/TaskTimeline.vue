<script setup lang="ts">
import { date } from 'quasar';
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { useUsers } from 'src/application/dynamic/queries/permissions-queries';
import { useWithdrawTaskComment } from 'src/application/dynamic/queries/tasks-queries';
import { mentionSegments } from 'src/domain/dynamic/task-mentions';
import type { TaskComment } from 'src/domain/dynamic/task-types';

const props = defineProps<{ taskId: number; comments: TaskComment[]; readonly?: boolean }>();
const { t } = useI18n();
const users = useUsers();
const withdraw = useWithdrawTaskComment();

const names = computed(() => new Map((users.data.value ?? []).map((u) => [u.userId, u.displayName])));
const nameOf = (id: unknown) => (typeof id === 'string' && id ? (names.value.get(id) ?? id) : t('tasks.unassigned'));
const timeText = (c: TaskComment) => date.formatDate(new Date(c.createdTime), 'YYYY-MM-DD HH:mm');
const sizeText = (n: number) => (n >= 1048576 ? `${(n / 1048576).toFixed(1)} MB` : `${Math.max(1, Math.round(n / 1024))} KB`);
const href = (c: TaskComment, aid: number) => `/api/v3/dynamic/tasks/${props.taskId}/comments/${c.id}/attachments/${aid}`;

/** System entries store codes; the text is composed here so every locale reads naturally. */
function systemText(c: TaskComment) {
  const ev = c.systemEvent ?? '';
  const { from, to } = c.systemData ?? {};
  const status = (v: unknown) => t(`tasks.status.${String(v)}`);
  const due = (v: unknown) => (v ? String(v) : t('tasks.noDue'));
  if (ev === 'assigneeChanged') return t('tasks.system.assigneeChanged', { from: nameOf(from), to: nameOf(to) });
  if (ev === 'dueChanged') return t('tasks.system.dueChanged', { from: due(from), to: due(to) });
  if (ev === 'statusChanged') return t('tasks.system.statusChanged', { from: status(from), to: status(to) });
  return ev ? t(`tasks.system.${ev}`) : '';
}

const segments = (c: TaskComment) => mentionSegments(c.body ?? '', c.mentions);
</script>

<template>
  <div data-testid="task-timeline">
    <div v-if="!comments.length" class="text-muted q-py-sm">{{ t('tasks.noComments') }}</div>
    <div v-for="c in comments" :key="c.id" class="q-py-xs" :data-testid="`comment-${c.id}`">
      <div class="row items-center no-wrap text-caption text-muted">
        <span v-if="c.authorName" class="text-weight-medium q-mr-sm">{{ c.authorName }}</span>
        <span>{{ timeText(c) }}</span>
        <q-space />
        <q-btn
          v-if="c.canWithdraw && !readonly" flat dense no-caps size="sm" :label="t('tasks.withdraw')"
          :loading="withdraw.isPending.value" :data-testid="`comment-withdraw-${c.id}`"
          @click="withdraw.mutate({ id: taskId, commentId: c.id })"
        />
      </div>
      <div v-if="c.kind === 'system'" class="text-body2 text-muted" style="overflow-wrap: anywhere">{{ systemText(c) }}</div>
      <div v-else-if="c.withdrawn" class="text-body2 text-muted text-italic">{{ t('tasks.withdrawn') }}</div>
      <template v-else>
        <div class="text-body2" style="white-space: pre-wrap; overflow-wrap: anywhere">
          <template v-for="(s, i) in segments(c)" :key="i">
            <span v-if="s.kind === 'text'">{{ s.text }}</span>
            <span v-else class="text-primary" :data-testid="`mention-${s.userId}`">@{{ s.active ? s.name : t('tasks.inactiveUser', { name: s.name }) }}</span>
          </template>
        </div>
        <div v-for="a in c.attachments" :key="a.id" class="row items-center no-wrap q-mt-xs">
          <q-icon name="attach_file" class="q-mr-xs" />
          <a :href="href(c, a.id)" target="_blank" rel="noopener" class="text-primary" style="overflow-wrap: anywhere" :data-testid="`comment-attachment-${a.id}`">{{ a.fileName }}</a>
          <span class="text-caption text-muted q-ml-sm">{{ sizeText(a.size) }}</span>
        </div>
      </template>
    </div>
  </div>
</template>
