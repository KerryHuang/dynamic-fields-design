<script setup lang="ts">
import { Dialog } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { useMe } from 'src/application/dynamic/queries/permissions-queries';
import type { I18nText, ListView } from 'src/domain/dynamic/types';

const props = defineProps<{ views: ListView[]; current?: ListView; modified: boolean }>();
const emit = defineEmits<{
  select: [id: number | null];
  saveAs: [value: { name: I18nText; shared: boolean; isDefault: boolean }];
  update: [];
  remove: [];
  setDefault: [];
}>();
const { t, locale } = useI18n();
const me = useMe();

const nameOf = (v: ListView) => resolveI18n(v.name, locale.value);
const canEdit = computed(() => !!props.current && (props.current.isOwner || (props.current.shared && !!me.data.value?.designer.edit)));

const saving = ref(false);
const form = ref({ name: '', shared: false, isDefault: false });
function openSaveAs() {
  form.value = { name: '', shared: false, isDefault: false };
  saving.value = true;
}
function submit() {
  const name = form.value.name.trim();
  if (!name) return;
  // zh-TW is the required locale; also keep the entered text under the UI locale
  saving.value = false;
  emit('saveAs', { name: { 'zh-TW': name, [locale.value]: name }, shared: form.value.shared, isDefault: form.value.isDefault });
}
function confirmRemove() {
  Dialog.create({ message: t('dynamic.view.confirmDelete'), cancel: true, persistent: true }).onOk(() => emit('remove'));
}
</script>

<template>
  <div class="row items-center q-gutter-sm">
    <q-btn outline no-caps icon="view_list" :label="current ? nameOf(current) : t('dynamic.view.none')" data-testid="view-btn">
      <q-menu>
        <q-list style="min-width: 14rem">
          <q-item v-close-popup clickable :active="!current" data-testid="view-none" @click="emit('select', null)">
            <q-item-section>{{ t('dynamic.view.none') }}</q-item-section>
          </q-item>
          <q-item v-for="v in views" :key="v.id" v-close-popup clickable :active="v.id === current?.id" @click="emit('select', v.id)">
            <q-item-section>{{ nameOf(v) }}</q-item-section>
            <q-item-section v-if="v.isDefault" side><q-badge outline :label="t('dynamic.view.defaultMark')" /></q-item-section>
          </q-item>
          <q-separator />
          <q-item v-close-popup clickable data-testid="view-save-as" @click="openSaveAs">
            <q-item-section>{{ t('dynamic.view.saveAs') }}</q-item-section>
          </q-item>
          <template v-if="canEdit">
            <q-item v-close-popup clickable data-testid="view-update" @click="emit('update')">
              <q-item-section>{{ t('dynamic.view.update') }}</q-item-section>
            </q-item>
            <q-item v-if="!current!.isDefault" v-close-popup clickable data-testid="view-set-default" @click="emit('setDefault')">
              <q-item-section>{{ t('dynamic.view.setDefault') }}</q-item-section>
            </q-item>
            <q-item v-close-popup clickable class="text-negative" data-testid="view-delete" @click="confirmRemove">
              <q-item-section>{{ t('dynamic.view.delete') }}</q-item-section>
            </q-item>
          </template>
        </q-list>
      </q-menu>
    </q-btn>
    <q-badge v-if="current && modified" color="warning" text-color="dark" :label="t('dynamic.view.modified')" data-testid="view-modified" />

    <q-dialog v-model="saving">
      <q-card style="min-width: 20rem">
        <q-card-section class="column q-gutter-sm">
          <q-input v-model="form.name" :label="t('dynamic.view.name')" outlined dense autofocus data-testid="view-name" @keyup.enter="submit" />
          <q-toggle v-if="me.data.value?.designer.edit" v-model="form.shared" :label="t('dynamic.view.shared')" />
          <q-toggle v-model="form.isDefault" :label="t('dynamic.view.makeDefault')" />
        </q-card-section>
        <q-card-actions align="right">
          <q-btn v-close-popup flat :label="t('dynamic.cancel')" />
          <q-btn color="primary" unelevated :label="t('dynamic.save')" :disable="!form.name.trim()" data-testid="view-submit" @click="submit" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </div>
</template>
