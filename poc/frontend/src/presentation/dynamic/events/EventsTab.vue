<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useDeleteEventMutation, useEvents } from 'src/application/dynamic/queries/events-queries';
import { useMe } from 'src/application/dynamic/queries/permissions-queries';
import { useWorkflowQuery } from 'src/application/dynamic/queries/workflow-queries';
import type { ModuleEvent } from 'src/domain/dynamic/events-ledger';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { ModuleDetail } from 'src/domain/dynamic/types';
import { leftoverMessages } from 'src/presentation/dynamic/designer/api-error';
import type { ApiError } from 'src/services/api/http';
import EventEditorDialog from './EventEditorDialog.vue';
import { joinList } from 'src/i18n/translate';

const props = defineProps<{ module: ModuleDetail }>();
const { t, locale } = useI18n();
const $q = useQuasar();

const key = computed(() => props.module.key);
// Only a standalone module can be an event source; the list is not even requested for the others.
const unsupported = computed(() => !!props.module.detailOf || props.module.kind === 'extension');
const query = useEvents(computed(() => (unsupported.value ? '' : key.value)));
const workflow = useWorkflowQuery(key);
const me = useMe();
const readonly = computed(() => !me.data.value?.designer.edit);
const events = computed(() => query.data.value ?? []);

const stateLabel = (id: number) => {
  const field = props.module.blocks.flatMap((b) => b.fields).find((f) => f.id === workflow.data.value?.statusFieldId);
  const option = field?.options.find((o) => o.id === id);
  return option ? resolveI18n(option.label, locale.value) : `#${id}`;
};
/** e.g. "Status transition: Draft → Shipped". */
function trigger(ev: ModuleEvent): string {
  const tr = ev.definition.trigger;
  const kind = t(`events.triggerKind.${tr.on}`);
  if (tr.on !== 'transition' || tr.toState == null) return kind;
  const from = tr.fromStates?.length ? tr.fromStates.map(stateLabel).join('、') : '*';
  return `${kind}：${from} → ${stateLabel(tr.toState)}`;
}

const dialog = ref(false);
const editing = ref<ModuleEvent | undefined>();
function open(ev?: ModuleEvent) {
  editing.value = ev;
  dialog.value = true;
}

const deleting = ref<ModuleEvent | null>(null);
const remove = useDeleteEventMutation();
function confirmDelete() {
  remove.mutate(deleting.value!.key, {
    onSuccess: () => { deleting.value = null; $q.notify({ type: 'positive', message: t('events.deleted') }); },
    onError: (e) => $q.notify({ type: 'negative', message: joinList(leftoverMessages(e as unknown as ApiError, [])) || t('designer.saveFailed') }),
  });
}
</script>

<template>
  <div v-if="unsupported" class="text-muted" data-testid="events-unsupported">{{ t('events.unsupported') }}</div>
  <div v-else class="column q-gutter-y-md" data-testid="events-tab">
    <div class="text-caption text-muted">{{ t('events.hint') }}</div>
    <div class="row items-center">
      <q-space />
      <q-btn color="primary" icon="add" :label="t('events.create')" :disable="readonly" data-testid="event-create" @click="open()" />
    </div>
    <div v-if="query.isError.value" class="text-negative">{{ t('dynamic.loadFailed') }}</div>
    <div v-else-if="query.isPending.value" class="text-muted">{{ t('designer.loading') }}</div>
    <div v-else-if="!events.length" class="text-muted">{{ t('events.empty') }}</div>
    <q-list v-else bordered separator>
      <q-item v-for="ev in events" :key="ev.key" :class="{ 'text-muted': !ev.active }" :data-testid="`event-row-${ev.key}`">
        <q-item-section>
          <q-item-label>
            {{ resolveI18n(ev.name, locale) }}
            <q-badge v-if="!ev.active" color="muted" class="q-ml-sm">{{ t('events.off') }}</q-badge>
          </q-item-label>
          <q-item-label caption>{{ ev.key }}</q-item-label>
          <q-item-label caption class="event-trigger">{{ trigger(ev) }} · {{ t('events.effectCount', { n: ev.definition.effects.length }) }}</q-item-label>
        </q-item-section>
        <q-item-section side>
          <div class="row no-wrap items-center">
            <q-btn flat dense color="primary" :label="t('designer.edit')" :disable="readonly" :data-testid="`event-edit-${ev.key}`" @click="open(ev)" />
            <q-btn flat dense color="negative" :label="t('dynamic.delete')" :disable="readonly" :data-testid="`event-delete-${ev.key}`" @click="deleting = ev" />
          </div>
        </q-item-section>
      </q-item>
    </q-list>

    <EventEditorDialog v-model="dialog" :module="module" :event="editing" />

    <q-dialog :model-value="deleting !== null" @update:model-value="deleting = null">
      <q-card class="event-delete">
        <q-card-section>{{ t('events.deleteConfirm', { name: deleting ? resolveI18n(deleting.name, locale) : '' }) }}</q-card-section>
        <q-card-actions align="right">
          <q-btn v-close-popup flat :label="t('dynamic.cancel')" />
          <q-btn color="negative" :label="t('dynamic.delete')" :loading="remove.isPending.value" data-testid="event-delete-confirm" @click="confirmDelete" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </div>
</template>

<style scoped>
.event-delete { width: 420px; max-width: 95vw; }
.event-trigger { overflow-wrap: anywhere; }
</style>
