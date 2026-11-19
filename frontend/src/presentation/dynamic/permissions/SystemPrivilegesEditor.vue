<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { DESIGNER_ACTIONS, inheritedSystemPrivileges } from 'src/domain/dynamic/system-privileges';
import type { DesignerFlags, SystemPrivilegeSource, SystemPrivileges } from 'src/domain/dynamic/types';
import { joinList } from 'src/i18n/translate';

/**
 * 群組、使用者編輯對話框的「系統權限」區塊（權限軸設計 §11）：載入與存回的都是直接設定；
 * 使用者另顯示經群組取得的唯讀一行，系統管理員顯示自動擁有的提示。
 */
const props = defineProps<{ modelValue: SystemPrivileges; sources?: SystemPrivilegeSource[]; systemAdmin?: boolean }>();
const emit = defineEmits<{ 'update:modelValue': [value: SystemPrivileges] }>();
const { t } = useI18n();

const setDesigner = (k: keyof DesignerFlags, v: boolean) =>
  emit('update:modelValue', { ...props.modelValue, designer: { ...props.modelValue.designer, [k]: v } });
const setPermissionAdmin = (v: boolean) => emit('update:modelValue', { ...props.modelValue, permissionAdmin: v });

const quoted = (names: string[]) => joinList(names.map((name) => t('systemPrivileges.groupQuoted', { name })));
const inherited = computed(() => {
  const s = inheritedSystemPrivileges(props.sources);
  const parts = [
    ...s.designer.map((d) => t('systemPrivileges.designerFrom', {
      actions: joinList(d.actions.map((a) => t(`designer.permissions.axes.${a}`))), groups: quoted([d.groupName]),
    })),
    ...(s.permissionAdmin.length ? [t('systemPrivileges.permissionAdminFrom', { groups: quoted(s.permissionAdmin) })] : []),
  ];
  return parts.length ? t('systemPrivileges.inherited', { list: parts.join(t('systemPrivileges.separator')) }) : '';
});
</script>

<template>
  <div class="column q-gutter-y-xs" data-testid="system-privileges">
    <div class="text-subtitle2">{{ t('systemPrivileges.title') }}</div>
    <div v-if="systemAdmin" class="text-warning" data-testid="system-privileges-admin-hint">{{ t('systemPrivileges.adminHint') }}</div>
    <div class="row items-center q-gutter-x-md">
      <span class="text-body2">{{ t('systemPrivileges.designer') }}</span>
      <q-checkbox
        v-for="a in DESIGNER_ACTIONS" :key="a" dense :model-value="modelValue.designer[a]" :label="t(`designer.permissions.axes.${a}`)"
        :data-testid="`sp-designer-${a}`" @update:model-value="(v: boolean) => setDesigner(a, v)"
      />
    </div>
    <q-toggle
      dense :model-value="modelValue.permissionAdmin" :label="t('systemPrivileges.permissionAdminToggle')" data-testid="sp-permission-admin"
      @update:model-value="setPermissionAdmin"
    />
    <div class="text-caption text-muted">{{ t('systemPrivileges.hint') }}</div>
    <div v-if="inherited" class="text-caption" style="overflow-wrap: anywhere" data-testid="system-privileges-inherited">{{ inherited }}</div>
  </div>
</template>
