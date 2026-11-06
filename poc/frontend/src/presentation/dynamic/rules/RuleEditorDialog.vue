<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import GenericSelect from 'src/components/framework/GenericSelect.vue';
import { isConditionGroup, pruneCondition } from 'src/domain/dynamic/condition';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { ConditionNode, FieldCondition, I18nText, Rule, RuleKind, SchemaField } from 'src/domain/dynamic/types';
import I18nTextInput from 'src/presentation/dynamic/designer/I18nTextInput.vue';
import ConditionEditor from 'src/presentation/dynamic/list/ConditionEditor.vue';

const props = defineProps<{
  modelValue: boolean;
  fields: SchemaField[];
  blocks: { id: number; label: I18nText }[];
  /** Edit this rule; absent means a new one. */
  rule?: Rule;
}>();
const emit = defineEmits<{ 'update:modelValue': [open: boolean]; save: [rule: Rule] }>();
const { t, locale } = useI18n();

const KINDS: RuleKind[] = ['visibility', 'required', 'readOnly', 'validation', 'optionFilter'];
const COMPUTED_TYPES = ['formula', 'autonumber', 'rollup'];

const kind = ref<RuleKind>('required');
const condition = ref<ConditionNode | undefined>(undefined);
const targets = ref<string[]>([]);
const optionIds = ref<number[]>([]);
const message = ref<I18nText>({});
const active = ref(true);
const error = ref('');
const messageInput = ref<{ validate(): Promise<boolean> } | null>(null);

watch(() => [props.modelValue, props.rule] as const, ([open]) => {
  if (!open) return;
  const r = props.rule;
  kind.value = r?.kind ?? 'required';
  condition.value = r ? JSON.parse(JSON.stringify(r.condition)) : undefined;
  targets.value = r ? [...r.targets] : [];
  optionIds.value = r ? [...r.optionIds] : [];
  message.value = r?.message ? { ...r.message } : {};
  active.value = r?.active ?? true;
  error.value = '';
}, { immediate: true });

function setKind(k: string | string[] | null) {
  if (!k || Array.isArray(k)) return;
  kind.value = k as RuleKind;
  targets.value = [];
  optionIds.value = [];
  error.value = '';
}

const kindOptions = computed(() => KINDS.map((value) => ({ value, label: t(`designer.rules.kinds.${value}`) })));
const label = (f: SchemaField) => resolveI18n(f.label, locale.value);
const fieldTargets = computed(() => props.fields.map((f) => ({ value: `field:${f.id}`, label: t('designer.rules.targetField', { name: label(f) }) })));
const targetOptions = computed(() => {
  if (kind.value === 'required' || kind.value === 'validation') return fieldTargets.value;
  const blockTargets = props.blocks.map((b) => ({ value: `block:${b.id}`, label: t('designer.rules.targetBlock', { name: resolveI18n(b.label, locale.value) }) }));
  const record = kind.value === 'readOnly' ? [{ value: '*', label: t('designer.rules.targetRecord') }] : [];
  return [...fieldTargets.value, ...blockTargets, ...record];
});
const dropdownTargets = computed(() => props.fields.filter((f) => f.type === 'enum' || f.type === 'multienum')
  .map((f) => ({ value: `field:${f.id}`, label: label(f) })));
const optionField = computed(() => props.fields.find((f) => `field:${f.id}` === targets.value[0]));

const optionId = (value: string) => Number(value.replace('opt_', ''));
function setOptionTarget(v: string | string[] | null) {
  targets.value = typeof v === 'string' ? [v] : [];
  optionIds.value = [];
}
function toggleOption(id: number, on: boolean) {
  optionIds.value = on ? [...optionIds.value, id] : optionIds.value.filter((x) => x !== id);
}

const referencedTypes = (n: ConditionNode | undefined): number[] => {
  if (!n) return [];
  if (isConditionGroup(n)) return ('all' in n ? n.all : n.any).flatMap(referencedTypes);
  const c = n as FieldCondition;
  const ref = typeof c.value === 'object' && c.value !== null && !Array.isArray(c.value) && 'fieldRef' in c.value
    ? [(c.value as { fieldRef: number }).fieldRef] : [];
  return [c.field, ...ref];
};
const usesComputed = computed(() => referencedTypes(condition.value).some((id) => {
  const f = props.fields.find((x) => x.id === id);
  return !!f && COMPUTED_TYPES.includes(f.type);
}));

async function submit() {
  error.value = '';
  if (targets.value.length === 0) { error.value = t('designer.rules.targetRequired'); return; }
  if (kind.value === 'optionFilter' && optionIds.value.length === 0) { error.value = t('designer.rules.optionRequired'); return; }
  if (kind.value === 'validation' && !(await messageInput.value?.validate())) return;
  const rule: Rule = {
    ...(props.rule?.id !== undefined ? { id: props.rule.id } : {}),
    kind: kind.value,
    condition: pruneCondition(condition.value) ?? { all: [] },
    targets: targets.value,
    optionIds: kind.value === 'optionFilter' ? optionIds.value : [],
    ...(kind.value === 'validation' ? { message: message.value } : {}),
    active: active.value,
  };
  emit('save', rule);
  emit('update:modelValue', false);
}
</script>

<template>
  <q-dialog :model-value="modelValue" persistent @update:model-value="(v) => emit('update:modelValue', v)">
    <q-card style="min-width: 640px; max-width: 95vw">
      <q-card-section class="text-h6">{{ rule ? t('designer.rules.edit') : t('designer.rules.add') }}</q-card-section>
      <q-card-section class="q-gutter-md scroll" style="max-height: 70vh">
        <GenericSelect :model-value="kind" :label="t('designer.rules.kindLabel')" :options="kindOptions" required data-testid="rule-kind" @update:model-value="setKind" />

        <div>
          <div class="text-subtitle2 q-mb-xs">{{ t('designer.rules.condition') }}</div>
          <ConditionEditor v-model="condition" :fields="fields" allow-field-ref />
          <div v-if="kind === 'validation'" class="text-muted text-caption q-mt-xs">{{ t('designer.rules.validationHint') }}</div>
          <div v-if="usesComputed" class="text-warning q-mt-xs" data-testid="rule-computed-hint">{{ t('designer.rules.computedHint') }}</div>
        </div>

        <template v-if="kind === 'optionFilter'">
          <GenericSelect
            :model-value="targets[0] ?? null" :label="t('designer.rules.optionTarget')" :options="dropdownTargets" required
            data-testid="rule-option-target" @update:model-value="setOptionTarget"
          />
          <div v-if="optionField">
            <div class="text-subtitle2">{{ t('designer.rules.options') }}</div>
            <div v-if="!optionField.options.length" class="text-muted">{{ t('designer.rules.noOptions') }}</div>
            <q-checkbox
              v-for="o in optionField.options" :key="o.value" :model-value="optionIds.includes(optionId(o.value))"
              :label="resolveI18n(o.label, locale) + (o.active ? '' : ` (${t('designer.rules.inactive')})`)" :data-testid="`rule-opt-${o.value}`"
              @update:model-value="(v) => toggleOption(optionId(o.value), !!v)"
            />
          </div>
        </template>
        <GenericSelect
          v-else :model-value="targets" :label="t('designer.rules.targets')" :options="targetOptions" multiple required
          data-testid="rule-targets" @update:model-value="(v) => (targets = Array.isArray(v) ? v : [])"
        />

        <div v-if="kind === 'validation'" data-testid="rule-message">
          <I18nTextInput ref="messageInput" v-model="message" :label="t('designer.rules.message')" />
        </div>

        <q-toggle v-model="active" :label="t('designer.rules.active')" />
        <div v-if="error" class="text-negative" data-testid="rule-error">{{ error }}</div>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat :label="t('designer.close')" data-testid="rule-cancel" @click="emit('update:modelValue', false)" />
        <q-btn color="primary" :label="t('designer.save')" data-testid="rule-save" @click="submit" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
