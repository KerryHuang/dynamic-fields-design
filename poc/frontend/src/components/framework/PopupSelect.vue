<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query';
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import type { LightItem, ReferenceInfo } from 'src/domain/dynamic/types';
import { dynamicApi } from 'src/services/api/dynamic-api';

type Key = string | number;
const PAGE_SIZE = 10;
const DEBOUNCE_MS = 300;

const props = defineProps<{
  modelValue: Key | Key[] | null | undefined;
  lightUrl: string;
  valueKind: 'number' | 'string';
  /** The record's reference info for the current single value; used only while its key still matches the value. */
  reference?: ReferenceInfo;
  /** 已知的標題（鍵為值的字串）；還沒開過挑選視窗時，已存的值靠這個顯示標題。 */
  knownTitles?: Record<string, string>;
  multiple?: boolean;
  label?: string;
  error?: string;
  readonly?: boolean;
  required?: boolean;
}>();
const emit = defineEmits<{ 'update:modelValue': [value: Key | Key[] | null] }>();
const { t } = useI18n();

const open = ref(false);
const keywordInput = ref('');
const keyword = ref('');
const page = ref(1);
const picked = ref<Key[]>([]);
const titles = ref(new Map<string, string>());

let timer: ReturnType<typeof setTimeout> | undefined;
watch(keywordInput, (v) => {
  clearTimeout(timer);
  timer = setTimeout(() => { keyword.value = v; page.value = 1; }, DEBOUNCE_MS);
});
onBeforeUnmount(() => clearTimeout(timer));

const { data, isError } = useQuery({
  queryKey: ['light', computed(() => props.lightUrl), keyword, page],
  queryFn: () => dynamicApi.light(props.lightUrl, { keyword: keyword.value, page: page.value, size: PAGE_SIZE }),
  enabled: open,
  placeholderData: (prev) => prev,
});
const items = computed(() => data.value?.items ?? []);
const pageCount = computed(() => Math.max(1, Math.ceil((data.value?.total ?? 0) / PAGE_SIZE)));
watch(items, (list) => list.forEach((i) => titles.value.set(i.key, i.title)));

const toValue = (key: string): Key => (props.valueKind === 'number' ? Number(key) : key);
const current = computed<Key[]>(() => [props.modelValue ?? []].flat());
const titleOf = (v: Key) => titles.value.get(String(v)) ?? props.knownTitles?.[String(v)] ?? String(v);
const display = computed(() => {
  if (props.multiple) return current.value.map(titleOf).join('、');
  const v = current.value[0];
  if (v === undefined) return '';
  const info = props.reference;
  if (info && info.key === String(v)) {
    if (info.state === 'missing') return t('dynamic.reference.missing');
    if (info.state === 'restricted') return t('dynamic.reference.restricted');
    if (info.title) return info.title;
  }
  return titleOf(v);
});

function show() {
  if (props.readonly) return;
  picked.value = [...current.value];
  open.value = true;
}
function choose(item: LightItem) {
  titles.value.set(item.key, item.title);
  if (!props.multiple) {
    emit('update:modelValue', toValue(item.key));
    open.value = false;
    return;
  }
  const v = toValue(item.key);
  picked.value = picked.value.includes(v) ? picked.value.filter((x) => x !== v) : [...picked.value, v];
}
function confirm() {
  emit('update:modelValue', picked.value);
  open.value = false;
}
function clear() {
  emit('update:modelValue', props.multiple ? [] : null);
  open.value = false;
}
const isPicked = (item: LightItem) => (props.multiple ? picked.value : current.value).map(String).includes(item.key);
</script>

<template>
  <q-input
    :model-value="display" :label="required ? `${label} *` : label" :error="!!error" :error-message="error"
    readonly outlined dense :class="{ 'cursor-pointer': !readonly }" data-testid="popup-input" @click="show"
  >
    <template #append>
      <q-icon v-if="!readonly" name="search" />
    </template>
  </q-input>
  <q-dialog v-model="open">
    <q-card style="min-width: 360px" data-testid="popup-dialog">
      <q-card-section>
        <q-input v-model="keywordInput" :placeholder="t('dynamic.reference.search')" outlined dense autofocus data-testid="popup-keyword" />
      </q-card-section>
      <q-list separator style="max-height: 50vh; overflow: auto">
        <q-item v-for="i in items" :key="i.key" v-ripple clickable :active="isPicked(i)" data-testid="popup-item" @click="choose(i)">
          <q-item-section>{{ i.title }}</q-item-section>
          <q-item-section v-if="isPicked(i)" side><q-icon name="check" /></q-item-section>
        </q-item>
        <q-item v-if="!items.length">
          <q-item-section class="text-muted">{{ isError ? t('dynamic.reference.restricted') : t('dynamic.reference.empty') }}</q-item-section>
        </q-item>
      </q-list>
      <q-card-section v-if="pageCount > 1" class="flex flex-center">
        <q-pagination v-model="page" :max="pageCount" :max-pages="5" direction-links dense />
      </q-card-section>
      <q-card-actions align="between">
        <q-btn flat :label="t('dynamic.reference.clear')" data-testid="popup-clear" @click="clear" />
        <q-btn v-if="multiple" color="primary" :label="t('dynamic.reference.confirm')" data-testid="popup-confirm" @click="confirm" />
        <q-btn v-else flat :label="t('dynamic.cancel')" @click="open = false" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
