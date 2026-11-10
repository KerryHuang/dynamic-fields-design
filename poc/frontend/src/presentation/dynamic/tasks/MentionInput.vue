<script setup lang="ts">
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useAttachmentSettings } from 'src/application/dynamic/queries/attachment-queries';
import { useAddTaskComment, useMentionCandidates } from 'src/application/dynamic/queries/tasks-queries';
import { encodeMentions, mentionQuery, type MentionPick } from 'src/domain/dynamic/task-mentions';
import type { ApiError } from 'src/services/api/http';

const MAX_FILES = 5;
const props = defineProps<{ taskId: number }>();
const { t } = useI18n();
const send = useAddTaskComment();
const settings = useAttachmentSettings();
const input = ref<{ getNativeElement(): HTMLTextAreaElement } | null>(null);

const text = ref('');
const picked = ref<MentionPick[]>([]);
const files = ref<File[]>([]);
const limitWarning = ref(false);
const rejected = ref('');
const error = ref('');
const keyword = ref<string | null>(null);
const candidates = useMentionCandidates(keyword);
const menuOpen = computed(() => keyword.value !== null);

const uploadOff = computed(() => settings.data.value?.uploadEnabled === false);
const reason = computed(() => settings.data.value?.disabledReason || t('tasks.attachmentsDisabled'));
const accept = computed(() => settings.data.value?.allowedExtensions.map((e) => `.${e}`).join(','));
const canSend = computed(() => !!text.value.trim() || files.value.length > 0);

function refreshQuery() {
  const el = input.value?.getNativeElement();
  keyword.value = el ? mentionQuery(text.value, el.selectionStart ?? text.value.length) : null;
}

function onText(v: string | number | null) {
  text.value = String(v ?? '');
  refreshQuery();
}

function pick(user: { userId: string; displayName: string }) {
  // Two people with one name are told apart by account, both in the text and in the stored pick.
  const twins = [...(candidates.data.value ?? []), ...picked.value].some((o) => o.userId !== user.userId && o.displayName === user.displayName);
  const label = twins ? `${user.displayName}(${user.userId})` : undefined;
  const el = input.value?.getNativeElement();
  const caret = el?.selectionStart ?? text.value.length;
  const before = text.value.slice(0, caret);
  const at = before.lastIndexOf('@');
  const inserted = `@${label ?? user.displayName} `;
  text.value = text.value.slice(0, at) + inserted + text.value.slice(caret);
  if (!picked.value.some((p) => p.userId === user.userId)) picked.value.push({ userId: user.userId, displayName: user.displayName, label });
  keyword.value = null;
  const pos = at + inserted.length;
  void Promise.resolve().then(() => { el?.focus(); el?.setSelectionRange(pos, pos); });
}

const maxBytes = computed(() => settings.data.value?.maxBytes);
const mb = (n: number) => `${(n / 1048576).toFixed(1)} MB`;
// q-file drops what breaks max-file-size / accept and reports it here.
function onRejected(entries: { failedPropValidation: string; file: File }[]) {
  rejected.value = entries
    .map((e) => (e.failedPropValidation === 'max-file-size'
      ? t('tasks.fileTooBig', { name: e.file.name, max: mb(maxBytes.value ?? 0) })
      : t('tasks.fileRejected', { name: e.file.name })))
    .join(t('common.listSeparator'));
}

function onFiles(v: File[] | null) {
  rejected.value = '';
  const list = v ?? [];
  limitWarning.value = list.length > MAX_FILES;
  files.value = list.slice(0, MAX_FILES);
}

async function submit() {
  if (!canSend.value) return;
  error.value = '';
  try {
    await send.mutateAsync({ id: props.taskId, body: encodeMentions(text.value.trim(), picked.value), files: files.value });
    text.value = '';
    picked.value = [];
    files.value = [];
    limitWarning.value = false;
    keyword.value = null;
  } catch (e) {
    const err = e as ApiError;
    // Field errors come keyed `files` / `body`; a 413 has no JSON at all, so it falls back to a generic message.
    if (err.status === 413) error.value = t('tasks.tooLarge');
    else error.value = (err.errors ? Object.values(err.errors).flat().join(t('common.listSeparator')) : err.message) || t('tasks.sendFailed');
  }
}
</script>

<template>
  <div class="q-mt-sm" data-testid="comment-composer">
    <q-input
      ref="input" :model-value="text" type="textarea" autogrow outlined dense maxlength="4000"
      :placeholder="t('tasks.commentPlaceholder')" data-testid="mention-input"
      @update:model-value="onText" @click="refreshQuery" @keyup.left="refreshQuery" @keyup.right="refreshQuery"
    >
      <q-menu :model-value="menuOpen" no-focus no-refocus no-parent-event fit anchor="bottom left" self="top left" @update:model-value="(v) => { if (!v) keyword = null; }">
        <q-list dense style="min-width: 200px">
          <q-item v-for="u in candidates.data.value ?? []" :key="u.userId" clickable :data-testid="`mention-option-${u.userId}`" @click="pick(u)">
            <q-item-section>
              <q-item-label>{{ u.displayName }}</q-item-label>
              <q-item-label caption>{{ u.userId }}</q-item-label>
            </q-item-section>
          </q-item>
          <q-item v-if="!(candidates.data.value ?? []).length"><q-item-section class="text-muted">{{ t('tasks.mentionNone') }}</q-item-section></q-item>
        </q-list>
      </q-menu>
    </q-input>
    <q-file
      :model-value="files" multiple append use-chips outlined dense class="q-mt-sm" :label="t('tasks.attach')"
      :accept="accept" :max-file-size="maxBytes" :disable="uploadOff" :hint="uploadOff ? undefined : t('tasks.attachLimit', { n: MAX_FILES })"
      data-testid="task-attach" @update:model-value="onFiles" @rejected="onRejected"
    >
      <template #prepend><q-icon name="attach_file" /></template>
    </q-file>
    <div v-if="uploadOff" class="text-caption text-warning q-mt-xs" data-testid="attach-off">{{ reason }}</div>
    <div v-if="rejected" class="text-caption text-negative q-mt-xs" data-testid="attach-rejected">{{ rejected }}</div>
    <div v-if="limitWarning" class="text-caption text-warning q-mt-xs" data-testid="attach-limit-warning">{{ t('tasks.attachLimitReached', { n: MAX_FILES }) }}</div>
    <div v-if="error" class="text-negative text-caption q-mt-xs" data-testid="comment-error">{{ error }}</div>
    <div class="row justify-end q-mt-sm">
      <q-btn color="primary" no-caps :label="t('tasks.send')" :disable="!canSend" :loading="send.isPending.value" data-testid="comment-send" @click="submit" />
    </div>
  </div>
</template>
