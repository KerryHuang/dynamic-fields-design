<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useMe } from 'src/application/dynamic/queries/permissions-queries';
import {
  useCreatePrintTemplate, useDeletePrintTemplate, usePrintTemplateDefinitions, useUpdatePrintTemplate,
} from 'src/application/dynamic/queries/print-queries';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { Locale, ModuleDetail, PrintTemplateDefinition } from 'src/domain/dynamic/types';
import { messagesOutside } from 'src/presentation/dynamic/designer/api-error';
import type { ApiError } from 'src/services/api/http';

const props = defineProps<{ module: ModuleDetail }>();
const { t, locale } = useI18n();
const $q = useQuasar();

const LOCALES: Locale[] = ['zh-TW', 'en-US', 'th', 'vi'];
const key = computed(() => props.module.key);
const query = usePrintTemplateDefinitions(key);
const me = useMe();
const create = useCreatePrintTemplate(key);
const update = useUpdatePrintTemplate(key);
const remove = useDeletePrintTemplate(key);

const readonly = computed(() => !me.data.value?.designer.edit);
// Detail modules print as the loop of their header's template; extensions have no records of their own to print.
const unsupported = computed(() => !!props.module.detailOf || props.module.kind === 'extension');
const fields = computed(() => props.module.blocks.flatMap((b) => b.fields).filter((f) => f.status === 'Active'));

const draft = ref<PrintTemplateDefinition | null>(null);
const isNew = ref(false);
const errors = ref<string[]>([]);
const body = ref<HTMLTextAreaElement | null>(null);
const loopModule = ref('');

watch(key, () => { draft.value = null; errors.value = []; });

const templates = computed(() => query.data.value ?? []);
const pick = (d: PrintTemplateDefinition) => { draft.value = { ...d, name: { ...d.name } }; isNew.value = false; errors.value = []; };
const start = () => {
  draft.value = { key: '', name: {}, body: '', active: true };
  isNew.value = true;
  errors.value = [];
};

function insert(text: string) {
  if (!draft.value || readonly.value) return;
  const el = body.value;
  const at = el?.selectionStart ?? draft.value.body.length;
  const end = el?.selectionEnd ?? at;
  draft.value = { ...draft.value, body: draft.value.body.slice(0, at) + text + draft.value.body.slice(end) };
}
const insertLoop = () => {
  const m = loopModule.value.trim();
  if (m) insert(`{{#each ${m}}}\n{{_index}}\n{{/each}}`);
};

function fail(e: unknown) {
  const err = e as ApiError;
  errors.value = messagesOutside(err.errors, []);
  if (!errors.value.length && err.message) errors.value = [err.message];
  if (!errors.value.length) $q.notify({ type: 'negative', message: t('designer.saveFailed') });
}
function submit() {
  const d = draft.value;
  if (!d) return;
  errors.value = [];
  const done = () => {
    $q.notify({ type: 'positive', message: t('print.saved') });
    isNew.value = false;
  };
  (isNew.value ? create : update).mutate(d, { onSuccess: done, onError: fail });
}
const confirmingRemove = ref(false);
function removeTemplate() {
  const d = draft.value;
  confirmingRemove.value = false;
  if (!d) return;
  remove.mutate(d.key, {
    onSuccess: () => { draft.value = null; $q.notify({ type: 'positive', message: t('print.removed') }); },
    onError: fail,
  });
}
</script>

<template>
  <div v-if="unsupported" class="text-muted" data-testid="print-unsupported">{{ t('print.unsupported') }}</div>
  <div v-else-if="query.isError.value" class="text-negative">{{ t('dynamic.loadFailed') }}</div>
  <div v-else-if="!me.data.value || query.isPending.value" class="text-muted">{{ t('designer.loading') }}</div>
  <div v-else class="row q-col-gutter-md" data-testid="print-tab">
    <div class="col-12 text-caption text-muted">{{ t('print.hint') }}</div>

    <div class="col-12 col-md-3">
      <q-list bordered separator dense>
        <q-item
          v-for="tpl in templates" :key="tpl.key" clickable :active="draft?.key === tpl.key && !isNew"
          :data-testid="`print-item-${tpl.key}`" @click="pick(tpl)"
        >
          <q-item-section>
            <q-item-label>{{ resolveI18n(tpl.name, locale) || tpl.key }}</q-item-label>
            <q-item-label caption>{{ tpl.key }}<template v-if="!tpl.active"> · {{ t('print.inactive') }}</template></q-item-label>
          </q-item-section>
        </q-item>
        <q-item v-if="!templates.length"><q-item-section class="text-muted">{{ t('print.none') }}</q-item-section></q-item>
      </q-list>
      <q-btn v-if="!readonly" class="q-mt-sm" outline color="primary" icon="add" :label="t('print.add')" data-testid="print-add" @click="start" />
    </div>

    <div class="col-12 col-md-9">
      <div v-if="!draft" class="text-muted">{{ t('print.pickOne') }}</div>
      <div v-else class="q-gutter-md" data-testid="print-editor">
        <q-input
          v-model="draft.key" :label="t('print.key')" :hint="t('print.keyHint')" :disable="readonly || !isNew"
          outlined dense maxlength="40" data-testid="print-key"
        />
        <div class="row q-col-gutter-sm">
          <div v-for="l in LOCALES" :key="l" class="col-12 col-sm-6">
            <q-input
              :model-value="draft.name[l] ?? ''" :label="`${t('print.name')} (${l})`" :disable="readonly" outlined dense
              :data-testid="`print-name-${l}`" @update:model-value="(v) => (draft!.name = { ...draft!.name, [l]: String(v ?? '') })"
            />
          </div>
        </div>
        <q-toggle v-model="draft.active" :label="t('print.active')" :disable="readonly" data-testid="print-active" />

        <div>
          <div class="text-caption text-muted q-mb-xs">{{ t('print.insertField') }}</div>
          <div class="row q-gutter-xs">
            <q-chip
              v-for="f in fields" :key="f.id" dense clickable :disable="readonly" :data-testid="`print-insert-${f.id}`"
              @click="insert(`{{${f.key}}}`)"
            >{{ resolveI18n(f.label, locale) }}</q-chip>
            <q-chip dense clickable outline :disable="readonly" @click="insert('{{_title}}')">{{ t('print.title') }}</q-chip>
            <q-chip dense clickable outline :disable="readonly" @click="insert('{{_printedAt}}')">{{ t('print.printedAt') }}</q-chip>
          </div>
          <div class="row items-center q-gutter-sm q-mt-xs">
            <q-input v-model="loopModule" dense outlined style="min-width: 220px" :label="t('print.loopModule')" :disable="readonly" data-testid="print-loop-module" />
            <q-btn flat dense color="primary" :label="t('print.insertLoop')" :disable="readonly || !loopModule.trim()" data-testid="print-insert-loop" @click="insertLoop" />
          </div>
        </div>

        <q-input
          ref="bodyInput" v-model="draft.body" type="textarea" :label="t('print.body')" :hint="t('print.bodyHint')" :disable="readonly"
          outlined autogrow input-style="font-family: monospace; min-height: 280px" data-testid="print-body"
          @vue:mounted="() => (body = ($refs.bodyInput as { nativeEl?: HTMLTextAreaElement } | undefined)?.nativeEl ?? null)"
        />

        <div v-if="errors.length" class="text-negative" data-testid="print-errors">
          <div v-for="e in errors" :key="e">{{ e }}</div>
        </div>
        <div v-if="!readonly" class="row q-gutter-sm">
          <q-btn color="primary" :label="t('print.save')" :loading="create.isPending.value || update.isPending.value" data-testid="print-save" @click="submit" />
          <q-btn v-if="!isNew" flat color="negative" :label="t('print.remove')" data-testid="print-remove" @click="confirmingRemove = true" />
        </div>
      </div>
    </div>

    <q-dialog v-model="confirmingRemove">
      <q-card>
        <q-card-section>{{ t('print.removeConfirm') }}</q-card-section>
        <q-card-actions align="right">
          <q-btn v-close-popup flat :label="t('dynamic.cancel')" />
          <q-btn color="negative" :label="t('print.remove')" data-testid="confirm-remove-print" @click="removeTemplate" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </div>
</template>
