<script setup lang="ts">
import { Notify } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRouter } from 'vue-router';
import { useInstantiateTemplate, useTemplates } from 'src/application/dynamic/queries/templates-queries';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { I18nText } from 'src/domain/dynamic/types';
import { leftoverMessages } from 'src/presentation/dynamic/designer/api-error';
import I18nTextInput from 'src/presentation/dynamic/designer/I18nTextInput.vue';
import { check, uncheck } from 'src/presentation/dynamic/designer/template-selection';
import type { TemplateFieldSummary, TemplateSummary } from 'src/services/api/dynamic-api';
import type { ApiError } from 'src/services/api/http';

const open = defineModel<boolean>({ required: true });
const { t, locale } = useI18n();
const router = useRouter();
const templates = useTemplates();
const instantiate = useInstantiateTemplate();

const picked = ref<TemplateSummary | null>(null);
const forms = ref<Record<string, { key: string; name: I18nText }>>({});
const selected = ref(new Set<string>());
const messages = ref<string[]>([]);

/** All modules' fields flattened with qualified codes, the shape template-selection works on. */
const flat = computed<TemplateFieldSummary[]>(() =>
  (picked.value?.modules ?? []).flatMap((m) => m.fields.map((f) => ({ ...f, code: `${m.code}.${f.code}` }))));

watch(open, (v) => { if (!v) picked.value = null; });

function pick(tpl: TemplateSummary) {
  picked.value = tpl;
  forms.value = Object.fromEntries(tpl.modules.map((m) => [m.code, { key: m.key, name: { ...m.name } }]));
  selected.value = new Set(tpl.modules.flatMap((m) => m.fields.map((f) => `${m.code}.${f.code}`)));
  messages.value = [];
}
const toggle = (code: string, on: boolean) => {
  selected.value = on ? check(flat.value, selected.value, code) : uncheck(flat.value, selected.value, code);
};
const headerName = (code: string) => {
  const h = picked.value?.modules.find((m) => m.code === code);
  return h ? resolveI18n(h.name, locale.value) : code;
};

function submit() {
  const tpl = picked.value!;
  messages.value = [];
  instantiate.mutate({
    key: tpl.key,
    body: {
      modules: tpl.modules.map((m) => ({ code: m.code, key: forms.value[m.code]!.key, name: forms.value[m.code]!.name })),
      excludeFields: flat.value.filter((f) => !selected.value.has(f.code)).map((f) => f.code),
    },
  }, {
    onSuccess: (r) => {
      for (const message of r.warnings) Notify.create({ type: 'warning', message });
      Notify.create({ type: 'positive', message: t('designer.template.created') });
      open.value = false;
      if (r.modules[0]) router.push(`/system/dynamic-modules/${r.modules[0].key}`);
    },
    onError: (e) => {
      const msgs = leftoverMessages(e as unknown as ApiError, []);
      messages.value = msgs.length ? msgs : [t('designer.saveFailed')];
    },
  });
}
</script>

<template>
  <q-dialog v-model="open" persistent>
    <q-card style="min-width: 560px; max-width: 90vw">
      <q-card-section class="text-h6">
        {{ picked ? resolveI18n(picked.name, locale) : t('designer.template.choose') }}
      </q-card-section>
      <q-card-section v-if="messages.length" class="text-negative" data-testid="template-error">
        <div v-for="m in messages" :key="m">{{ m }}</div>
      </q-card-section>

      <q-card-section v-if="!picked" class="q-pt-none">
        <q-list bordered separator>
          <q-item v-for="tpl in templates.data.value ?? []" :key="tpl.key" clickable :data-testid="`template-${tpl.key}`" @click="pick(tpl)">
            <q-item-section>
              <q-item-label>{{ resolveI18n(tpl.name, locale) }}</q-item-label>
              <q-item-label v-if="tpl.description" caption>{{ resolveI18n(tpl.description, locale) }}</q-item-label>
            </q-item-section>
            <q-item-section side>
              <q-badge outline>{{ t(tpl.source === 'builtIn' ? 'designer.template.builtIn' : 'designer.template.imported') }}</q-badge>
            </q-item-section>
          </q-item>
          <q-item v-if="templates.isSuccess.value && !templates.data.value?.length">
            <q-item-section class="text-muted">{{ t('designer.template.noTemplates') }}</q-item-section>
          </q-item>
        </q-list>
      </q-card-section>

      <q-card-section v-else class="q-pt-none q-gutter-md" style="max-height: 60vh; overflow: auto">
        <q-card v-for="m in picked.modules" :key="m.code" flat bordered :data-testid="`module-card-${m.code}`">
          <q-card-section class="q-gutter-sm">
            <div class="text-subtitle2">
              {{ m.code }}
              <q-badge v-if="m.detailOf" outline color="primary" class="q-ml-sm">{{ t('designer.template.detailOf', { header: headerName(m.detailOf) }) }}</q-badge>
            </div>
            <q-input v-model="forms[m.code]!.key" :label="t('designer.key')" outlined dense :data-testid="`module-key-${m.code}`" />
            <I18nTextInput v-model="forms[m.code]!.name" :label="t('designer.name')" />
            <div class="text-caption text-muted">{{ t('designer.template.fields') }}</div>
            <div v-for="f in m.fields" :key="f.code">
              <q-checkbox
                dense :model-value="selected.has(`${m.code}.${f.code}`)" :disable="f.locked"
                :label="resolveI18n(f.label, locale) + (f.locked ? `（${t('designer.template.locked')}）` : '')"
                :data-testid="`field-${m.code}.${f.code}`"
                @update:model-value="(v: boolean) => toggle(`${m.code}.${f.code}`, v)"
              />
            </div>
          </q-card-section>
        </q-card>
      </q-card-section>

      <q-card-actions align="right">
        <q-btn v-if="picked" flat :label="t('designer.template.back')" @click="picked = null" />
        <q-btn flat :label="t('designer.close')" @click="open = false" />
        <q-btn v-if="picked" color="primary" :label="t('designer.template.create')" :loading="instantiate.isPending.value" data-testid="template-create" @click="submit" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
