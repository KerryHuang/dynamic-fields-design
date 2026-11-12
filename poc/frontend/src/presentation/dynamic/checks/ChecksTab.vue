<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useChecks, useDeleteCheckMutation } from 'src/application/dynamic/queries/checks-queries';
import { useMe } from 'src/application/dynamic/queries/permissions-queries';
import { limitText } from 'src/domain/dynamic/check-draft';
import type { CheckOp, ModuleCheck } from 'src/domain/dynamic/events-ledger';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { ModuleDetail } from 'src/domain/dynamic/types';
import { leftoverMessages } from 'src/presentation/dynamic/designer/api-error';
import type { ApiError } from 'src/services/api/http';
import CheckEditorDialog from './CheckEditorDialog.vue';
import { joinList } from 'src/i18n/translate';

const OP_SYMBOLS: Record<CheckOp, string> = { gte: '≥', gt: '>', lte: '≤', lt: '<', eq: '=' };

const props = defineProps<{ module: ModuleDetail }>();
const { t, locale } = useI18n();
const $q = useQuasar();

const key = computed(() => props.module.key);
const query = useChecks(key);
const me = useMe();
const readonly = computed(() => !me.data.value?.designer.edit);
const checks = computed(() => query.data.value ?? []);

const fieldLabel = (id: number) => {
  const f = props.module.blocks.flatMap((b) => b.fields).find((x) => x.id === id);
  return f ? resolveI18n(f.label, locale.value) : `#${id}`;
};
/** e.g. "Sum(Qty) ≥ 0": what the check demands, in the designer's words. */
function rule(c: ModuleCheck): string {
  const d = c.definition;
  const fn = t(`checks.fn.${d.function}`);
  const subject = d.function === 'sum' && d.fieldId != null ? `${fn}(${fieldLabel(d.fieldId)})` : fn;
  return `${subject} ${OP_SYMBOLS[d.assert.op]} ${limitText(d.assert.limit, fieldLabel)}`;
}

const dialog = ref(false);
const editing = ref<ModuleCheck | undefined>();
function open(c?: ModuleCheck) {
  editing.value = c;
  dialog.value = true;
}

const deleting = ref<ModuleCheck | null>(null);
const remove = useDeleteCheckMutation();
function confirmDelete() {
  remove.mutate(deleting.value!.key, {
    onSuccess: () => { deleting.value = null; $q.notify({ type: 'positive', message: t('checks.deleted') }); },
    onError: (e) => $q.notify({ type: 'negative', message: joinList(leftoverMessages(e as unknown as ApiError, [])) || t('designer.saveFailed') }),
  });
}
</script>

<template>
  <div class="column q-gutter-y-md" data-testid="checks-tab">
    <div class="text-caption text-muted">{{ t('checks.hint') }}</div>
    <div class="row items-center">
      <q-space />
      <q-btn color="primary" icon="add" :label="t('checks.create')" :disable="readonly" data-testid="check-create" @click="open()" />
    </div>
    <div v-if="query.isError.value" class="text-negative">{{ t('dynamic.loadFailed') }}</div>
    <div v-else-if="query.isPending.value" class="text-muted">{{ t('designer.loading') }}</div>
    <div v-else-if="!checks.length" class="text-muted">{{ t('checks.empty') }}</div>
    <q-list v-else bordered separator>
      <q-item
        v-for="c in checks" :key="c.key" :clickable="!readonly" :class="{ 'text-muted': !c.active }" :data-testid="`check-row-${c.key}`"
        @click="!readonly && open(c)"
      >
        <q-item-section>
          <q-item-label>
            {{ resolveI18n(c.name, locale) }}
            <q-badge v-if="!c.active" color="muted" class="q-ml-sm">{{ t('checks.off') }}</q-badge>
          </q-item-label>
          <q-item-label caption>{{ c.key }}</q-item-label>
          <q-item-label caption class="check-rule">{{ rule(c) }}</q-item-label>
        </q-item-section>
        <q-item-section side>
          <q-btn
            v-if="$q.screen.lt.sm" flat round dense color="negative" icon="delete" :aria-label="t('dynamic.delete')" :disable="readonly"
            :data-testid="`check-delete-${c.key}`" @click.stop="deleting = c"
          />
          <q-btn
            v-else flat dense color="negative" :label="t('dynamic.delete')" :disable="readonly" :data-testid="`check-delete-${c.key}`"
            @click.stop="deleting = c"
          />
        </q-item-section>
      </q-item>
    </q-list>

    <CheckEditorDialog v-model="dialog" :module="module" :check="editing" />

    <q-dialog :model-value="deleting !== null" @update:model-value="deleting = null">
      <q-card class="check-delete">
        <q-card-section>{{ t('checks.deleteConfirm', { name: deleting ? resolveI18n(deleting.name, locale) : '' }) }}</q-card-section>
        <q-card-actions align="right">
          <q-btn v-close-popup flat :label="t('dynamic.cancel')" />
          <q-btn color="negative" :label="t('dynamic.delete')" :loading="remove.isPending.value" data-testid="check-delete-confirm" @click="confirmDelete" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </div>
</template>

<style scoped>
.check-delete { width: 420px; max-width: 95vw; }
.check-rule { overflow-wrap: anywhere; }
</style>
