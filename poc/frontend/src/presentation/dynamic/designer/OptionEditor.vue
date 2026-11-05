<script setup lang="ts">
import { useQuasar } from 'quasar';
import { reactive, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useDesignerMutation } from 'src/application/dynamic/queries/modules-queries';
import type { DesignerOption, I18nText } from 'src/domain/dynamic/types';
import { dynamicApi } from 'src/services/api/dynamic-api';
import type { ApiError } from 'src/services/api/http';
import { errorFor, notifyApiError } from './api-error';
import I18nTextInput from './I18nTextInput.vue';

interface Draft { label: I18nText; color: string; active: boolean }

const props = defineProps<{ moduleKey: string; fieldId: number; options: DesignerOption[] }>();
const { t } = useI18n();
const $q = useQuasar();

const drafts = reactive<Record<number, Draft>>({});
const fresh = ref<Draft>({ label: {}, color: '', active: true });

// Seed only rows we have not seen, so saving one row never overwrites another row's unsaved edits.
const seed = (o: DesignerOption) => { drafts[o.id] = { label: { ...o.label }, color: o.color ?? '', active: o.active }; };
watch(() => props.options, (list) => { for (const o of list) if (!drafts[o.id]) seed(o); }, { immediate: true, deep: true });

// Option-level 400s are shown on the row's label input (key `Label`); everything else goes to Notify.
const errors = reactive<Record<number, ApiError | null>>({});
const freshError = ref<ApiError | null>(null);
const fail = (e: unknown, id?: number) => {
  const err = e as ApiError;
  if (id === undefined) freshError.value = err; else errors[id] = err;
  notifyApiError((o) => $q.notify(o), err, ['Label'], t('designer.saveFailed'));
};
const add = useDesignerMutation(
  (d: Draft) => dynamicApi.addOption(props.fieldId, { label: d.label, color: d.color || undefined }),
  () => props.moduleKey,
);
const update = useDesignerMutation(
  (v: { id: number; d: Draft }) => dynamicApi.updateOption(v.id, { label: v.d.label, color: v.d.color || undefined, active: v.d.active }),
  () => props.moduleKey,
);
const onAdd = () => add.mutate(fresh.value, { onSuccess: () => { fresh.value = { label: {}, color: '', active: true }; freshError.value = null; }, onError: (e) => fail(e) });
</script>

<template>
  <div data-testid="option-editor">
    <div class="text-subtitle2 q-mb-sm">{{ t('designer.options') }}</div>
    <div v-for="o in options" :key="o.id" class="row items-start q-col-gutter-sm q-mb-sm">
      <div class="col">
        <I18nTextInput v-model="drafts[o.id]!.label" :label="t('designer.optionLabel')" :error="errorFor(errors[o.id] ?? null, 'Label')" />
      </div>
      <div class="col-3">
        <q-input v-model="drafts[o.id]!.color" :label="t('designer.color')" outlined dense clearable>
          <template #append>
            <q-icon name="colorize" class="cursor-pointer">
              <q-popup-proxy><q-color v-model="drafts[o.id]!.color" format-model="hex" no-footer /></q-popup-proxy>
            </q-icon>
          </template>
        </q-input>
      </div>
      <div class="col-auto">
        <q-toggle v-model="drafts[o.id]!.active" :label="t('designer.optionActive')" />
        <q-btn
          flat dense color="primary" icon="save" :title="t('designer.saveOption')"
          :disable="!drafts[o.id]!.label['zh-TW']" @click="update.mutate({ id: o.id, d: drafts[o.id]! }, { onSuccess: () => { errors[o.id] = null; }, onError: (e) => fail(e, o.id) })"
        />
      </div>
    </div>
    <div class="row items-start q-col-gutter-sm">
      <div class="col"><I18nTextInput v-model="fresh.label" :label="t('designer.optionLabel')" :error="errorFor(freshError, 'Label')" /></div>
      <div class="col-3"><q-input v-model="fresh.color" :label="t('designer.color')" outlined dense /></div>
      <div class="col-auto">
        <q-btn color="primary" icon="add" :label="t('designer.addOption')" :disable="!fresh.label['zh-TW']" @click="onAdd" />
      </div>
    </div>
  </div>
</template>
