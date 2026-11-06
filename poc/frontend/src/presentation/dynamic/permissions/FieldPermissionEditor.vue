<script setup lang="ts">
import type { QSelect } from 'quasar';
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import SelectNoOption from 'src/components/framework/SelectNoOption.vue';
import { useSelectFilter } from 'src/components/framework/useSelectFilter';
import type { DesignerField, Group, ModulePermissions } from 'src/domain/dynamic/types';

type Access = 'default' | 'readOnly' | 'hidden';

const props = defineProps<{
  modelValue: ModulePermissions;
  fields: DesignerField[];
  groups: Group[];
  titleFieldId?: number;
  readonly: boolean;
  /** Backend 400 errors keyed by JSON path (e.g. `fields[0].access`, `options[1].groupIds`); used to mark rows. */
  errors?: Record<string, string[]>;
}>();
const emit = defineEmits<{ 'update:modelValue': [value: ModulePermissions] }>();
const { t, locale } = useI18n();

const activeFields = computed(() => props.fields.filter((f) => f.status === 'Active'));
const optionFields = computed(() => activeFields.value.filter((f) => f.type === 'enum' || f.type === 'multienum'));
const groupOptions = computed(() => props.groups.map((g) => ({ value: g.id, label: resolveI18n(g.name, locale.value) })));
const accessSel = useSelectFilter<{ value: Access; label: string }>([]);
const scopeSel = useSelectFilter(groupOptions);

// The title field can't be saved as hidden, but an existing hidden row must still display as 隱藏.
const accessOptions = (f: DesignerField, groupId: number) => (['default', 'readOnly', 'hidden'] as Access[])
  .filter((a) => !(a === 'hidden' && f.id === props.titleFieldId && accessOf(f.id, groupId) !== 'hidden'))
  .map((value) => ({ value, label: t(`designer.permissions.access.${value}`) }));
const accessOf = (fieldId: number, groupId: number): Access =>
  props.modelValue.fields.find((p) => p.fieldId === fieldId && p.groupId === groupId)?.access ?? 'default';

function setAccess(fieldId: number, groupId: number, access: Access | null) {
  const rest = props.modelValue.fields.filter((p) => !(p.fieldId === fieldId && p.groupId === groupId));
  emit('update:modelValue', {
    ...props.modelValue,
    fields: access && access !== 'default' ? [...rest, { fieldId, groupId, access }] : rest,
  });
}

const scopeOf = (optionId: number) => props.modelValue.options.find((o) => o.optionId === optionId)?.groupIds ?? [];
// 多選挑完一項後清掉輸入字。
const scopeEls = new Map<number, QSelect | null>();
function onScope(optionId: number, groupIds: number[] | null) {
  setScope(optionId, groupIds);
  scopeEls.get(optionId)?.updateInputValue('');
}
function setScope(optionId: number, groupIds: number[] | null) {
  const rest = props.modelValue.options.filter((o) => o.optionId !== optionId);
  emit('update:modelValue', {
    ...props.modelValue,
    options: groupIds?.length ? [...rest, { optionId, groupIds: [...groupIds] }] : rest,
  });
}

function errorsAt(prefix: string): string | undefined {
  const hit = Object.entries(props.errors ?? {}).filter(([k]) => k === prefix || k.startsWith(`${prefix}.`)).flatMap(([, v]) => v);
  return hit.length ? hit.join('、') : undefined;
}
const cellError = (fieldId: number, groupId: number) => {
  const i = props.modelValue.fields.findIndex((p) => p.fieldId === fieldId && p.groupId === groupId);
  return i < 0 ? undefined : errorsAt(`fields[${i}]`);
};
const optionError = (optionId: number) => {
  const i = props.modelValue.options.findIndex((o) => o.optionId === optionId);
  return i < 0 ? undefined : errorsAt(`options[${i}]`);
};
</script>

<template>
  <div class="q-gutter-y-lg">
    <section>
      <div class="text-subtitle1">{{ t('designer.permissions.fields') }}</div>
      <div class="text-caption text-muted q-mb-sm">{{ t('designer.permissions.pricingHint') }}</div>
      <q-markup-table flat bordered dense>
        <thead>
          <tr>
            <th class="text-left">{{ t('designer.permissions.fields') }}</th>
            <th v-for="g in groups" :key="g.id">{{ resolveI18n(g.name, locale) }}</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="f in activeFields" :key="f.id">
            <td class="text-left">{{ resolveI18n(f.label, locale) }}</td>
            <td v-for="g in groups" :key="g.id" :class="{ 'bg-negative-soft': cellError(f.id, g.id) }" style="min-width: 120px">
              <q-select :model-value="accessOf(f.id, g.id)" :options="accessSel.filterBy(accessOptions(f, g.id))" :disable="readonly" emit-value map-options
                dense outlined :data-testid="`fp-${f.id}-${g.id}`" @update:model-value="setAccess(f.id, g.id, $event)" use-input input-debounce="0" fill-input hide-selected @filter="accessSel.onFilter" @popup-hide="accessSel.reset">
                <template #no-option><SelectNoOption /></template>
              </q-select>
              <div v-if="cellError(f.id, g.id)" class="text-negative text-caption" :data-testid="`fp-error-${f.id}-${g.id}`">
                {{ cellError(f.id, g.id) }}
              </div>
            </td>
          </tr>
        </tbody>
      </q-markup-table>
    </section>

    <section>
      <div class="text-subtitle1">{{ t('designer.permissions.options') }}</div>
      <div class="text-caption text-muted q-mb-sm">{{ t('designer.permissions.optionsHint') }}</div>
      <q-markup-table flat bordered dense>
        <tbody>
          <template v-for="f in optionFields" :key="f.id">
            <tr>
              <th colspan="2" class="text-left">{{ resolveI18n(f.label, locale) }}</th>
            </tr>
            <tr v-for="o in f.options" :key="o.id" :class="{ 'bg-negative-soft': optionError(o.id) }">
              <td class="text-left">{{ resolveI18n(o.label, locale) }}</td>
              <td style="min-width: 260px">
                <q-select :ref="(el) => scopeEls.set(o.id, el as QSelect | null)" :model-value="scopeOf(o.id)" :options="scopeSel.filtered.value" :disable="readonly" multiple use-chips emit-value map-options
                  dense outlined :data-testid="`opt-${o.id}`" @update:model-value="onScope(o.id, $event)" use-input input-debounce="0" @filter="scopeSel.onFilter" @popup-hide="scopeSel.reset">
                  <template #no-option><SelectNoOption /></template>
                </q-select>
                <div v-if="optionError(o.id)" class="text-negative text-caption" :data-testid="`opt-error-${o.id}`">{{ optionError(o.id) }}</div>
              </td>
            </tr>
          </template>
        </tbody>
      </q-markup-table>
    </section>
  </div>
</template>
