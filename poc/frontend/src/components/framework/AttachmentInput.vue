<script setup lang="ts">
import { computed, inject, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useQuasar } from 'quasar';
import { useAttachmentSettings } from 'src/application/dynamic/queries/attachment-queries';
import type { AttachmentFile } from 'src/domain/dynamic/types';
import { attachmentContextKey } from 'src/presentation/dynamic/attachment-context';
import { dynamicApi } from 'src/services/api/dynamic-api';
import type { ApiError } from 'src/services/api/http';

// Removing a file only edits the form value (saved with the record); uploading is its own request and
// needs a saved record, because the server records a file by adding it to the stored value.
const props = defineProps<{
  modelValue: AttachmentFile[] | null | undefined;
  label: string;
  fieldId: number;
  error?: string;
  readonly?: boolean;
  required?: boolean;
  /** Overrides the form context (the read-only detail page has none). */
  recordId?: number | null;
}>();
const emit = defineEmits<{ 'update:modelValue': [value: AttachmentFile[]] }>();
const { t } = useI18n();
const $q = useQuasar();
const context = inject(attachmentContextKey, undefined);
const settings = useAttachmentSettings();
const busy = ref(false);
const picker = ref<HTMLInputElement>();

const files = computed(() => props.modelValue ?? []);
const recordId = computed(() => props.recordId ?? context?.recordId ?? null);
const uploadOff = computed(() => settings.data.value?.uploadEnabled === false);
const canUpload = computed(() => !props.readonly && !!context && recordId.value !== null);
const accept = computed(() => settings.data.value?.allowedExtensions.map((e) => `.${e}`).join(','));
const reason = computed(() => settings.data.value?.disabledReason || t('attachment.uploadOffDefault'));
const sizeText = (n: number) => (n >= 1048576 ? `${(n / 1048576).toFixed(1)} MB` : `${Math.max(1, Math.round(n / 1024))} KB`);

function notifyFailure(e: unknown) {
  const err = e as ApiError;
  const detail = err.errors ? Object.values(err.errors).flat().join('、') : err.message;
  $q.notify({ type: 'negative', message: detail || t('attachment.failed') });
}

async function download(file: AttachmentFile) {
  if (recordId.value === null) return;
  try {
    const blob = await dynamicApi.downloadAttachment(recordId.value, props.fieldId, file.id);
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = file.name;
    a.click();
    URL.revokeObjectURL(url);
  } catch (e) {
    notifyFailure(e);
  }
}

async function onPick(e: Event) {
  const input = e.target as HTMLInputElement;
  const file = input.files?.[0];
  input.value = '';
  if (!file || recordId.value === null) return;
  busy.value = true;
  try {
    const { rowVersion, ...uploaded } = await dynamicApi.uploadAttachment(recordId.value, props.fieldId, file);
    context?.onUploaded(rowVersion);
    emit('update:modelValue', [...files.value, uploaded]);
  } catch (err) {
    notifyFailure(err);
  } finally {
    busy.value = false;
  }
}

const remove = (id: string) => emit('update:modelValue', files.value.filter((f) => f.id !== id));
</script>

<template>
  <div :data-testid="`attachment-${fieldId}`">
    <div class="text-caption text-muted">{{ required ? `${label} *` : label }}</div>
    <div v-if="!files.length" class="text-muted q-py-xs">{{ t('attachment.none') }}</div>
    <q-list v-else dense>
      <q-item v-for="f in files" :key="f.id" class="q-px-none">
        <q-item-section avatar><q-icon name="attach_file" /></q-item-section>
        <q-item-section>
          <a href="#" class="text-primary" data-testid="attachment-download" @click.prevent="download(f)">{{ f.name }}</a>
          <q-item-label caption>{{ sizeText(f.size) }}</q-item-label>
        </q-item-section>
        <q-item-section v-if="!readonly" side>
          <q-btn flat dense round icon="close" :aria-label="t('attachment.remove')" data-testid="attachment-remove" @click="remove(f.id)" />
        </q-item-section>
      </q-item>
    </q-list>
    <template v-if="!readonly">
      <template v-if="canUpload && !uploadOff">
        <q-btn flat dense color="primary" icon="upload" :label="t('attachment.upload')" :loading="busy" data-testid="attachment-upload" @click="picker?.click()" />
        <input ref="picker" type="file" hidden :accept="accept" data-testid="attachment-file" @change="onPick">
      </template>
      <div v-else-if="canUpload && uploadOff" class="text-caption text-warning" data-testid="attachment-upload-off">{{ reason }}</div>
      <div v-else-if="!context || recordId === null" class="text-caption text-muted">{{ t('attachment.saveFirst') }}</div>
    </template>
    <div v-if="error" class="text-negative text-caption">{{ error }}</div>
  </div>
</template>
