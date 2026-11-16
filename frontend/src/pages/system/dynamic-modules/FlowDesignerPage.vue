<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { onBeforeRouteLeave, useRoute, useRouter } from 'vue-router';
import { useApprovalFlow } from 'src/application/dynamic/queries/approval-flow-queries';
import { useModuleDetail } from 'src/application/dynamic/queries/modules-queries';
import { useMe } from 'src/application/dynamic/queries/permissions-queries';
import { useWorkflowQuery } from 'src/application/dynamic/queries/workflow-queries';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { toSchemaFields } from 'src/presentation/dynamic/designer/schema-fields';
import DesignerCanvas from 'src/presentation/dynamic/approval-designer/graph/DesignerCanvas.vue';
import DesignerPropertyPanel from 'src/presentation/dynamic/approval-designer/graph/DesignerPropertyPanel.vue';
import DesignerToolbar from 'src/presentation/dynamic/approval-designer/graph/DesignerToolbar.vue';
import { FLOW_SELECTION, toVueFlow } from 'src/presentation/dynamic/approval-designer/graph/designer-graph';
import { useGraphDesigner } from 'src/presentation/dynamic/approval-designer/graph/use-graph-designer';
import type { ApiError } from 'src/services/api/http';

// 流程圖設計頁（§10.2）：路由層動態載入的獨立頁面，Vue Flow 只在畫布、節點、線三個檔案。
const { t, locale } = useI18n();
const $q = useQuasar();
const route = useRoute();
const router = useRouter();

const moduleKey = computed(() => String(route.params.moduleKey));
const isNewRoute = computed(() => route.name === 'flow-designer-new');
const flowKey = computed(() => (isNewRoute.value ? null : String(route.params.flowKey)));
const target = computed(() => (route.query.target ? Number(route.query.target) : null));

const moduleQuery = useModuleDetail(moduleKey);
const workflow = useWorkflowQuery(moduleKey);
const flowQuery = useApprovalFlow(flowKey);
const me = useMe();

/** U62：只有讀取權的設計者可以進來看，畫布與面板唯讀。 */
const canEdit = computed(() => !!me.data.value?.designer.edit);
const designer = useGraphDesigner(() => canEdit.value);
const fields = computed(() => (moduleQuery.data.value ? toSchemaFields(moduleQuery.data.value) : []));

// ---- 載入：第一次拿到資料、或換了另一條流程時載入；之後的重抓（存檔後 invalidate）不覆蓋工作副本 ----
const loadFailed = ref(false);
const notFound = computed(() => (flowQuery.error.value as ApiError | null)?.status === 404);
watch([() => flowQuery.data.value, flowKey, () => !!me.data.value], ([flow, key, ready]) => {
  if (!ready) return;
  if (key === null) {
    if (!designer.loaded.value || !designer.server.value.isNew) { designer.load(null, target.value); loadFailed.value = false; }
    return;
  }
  if (!flow || flow.key !== key) return;
  // 網址的模組和流程實際所屬模組不一致（流程代碼全站唯一）：換到正確網址
  if (flow.moduleKey !== moduleKey.value) { void router.replace({ name: 'flow-designer', params: { moduleKey: flow.moduleKey, flowKey: key }, query: route.query }); return; }
  if (designer.loaded.value && designer.server.value.key === key) return;
  loadFailed.value = !designer.load(flow);
}, { immediate: true });

const meta = designer.meta;
const title = computed(() => {
  if (!designer.loaded.value) return t('approval.designer.graph.title');
  const name = resolveI18n(meta.value?.name, locale.value);
  return name || meta.value?.key || t('approval.designer.graph.titleNew');
});

// ---- 版面：手機（<600）全螢幕對話框、平板（600–1023）右側抽屜、筆電以上右側固定面板（桌機較寬，§10.5）----
const phone = computed(() => $q.screen.lt.sm);
const tablet = computed(() => $q.screen.gt.xs && $q.screen.lt.md);
const docked = computed(() => !phone.value && !tablet.value);
const panelWidth = computed(() => ($q.screen.gt.lg ? 440 : 360));
const panelOpen = ref(false);
const showPanel = computed(() => docked.value || panelOpen.value);
const selection = designer.selection;

const canvas = ref<{ fitAll(): void; focusNode(id: string, rightInset?: number): void } | null>(null);
const vf = computed(() => {
  const state = designer.state.value;
  if (!state) return { nodes: [], edges: [] };
  return toVueFlow(state, { locale: locale.value, t, fields: fields.value, issues: null, selection: selection.value }, { draggable: canEdit.value && !phone.value });
});
const canvasId = computed(() => (designer.server.value.isNew ? 'designer-new' : `designer-${designer.server.value.key}`));

async function open(sel: typeof selection.value) {
  designer.select(sel);
  if (!docked.value) panelOpen.value = true;
  if (sel.kind === 'node' && tablet.value) { await nextTick(); canvas.value?.focusNode(sel.id, panelWidth.value); }
}
const onPaneClick = () => { designer.select(FLOW_SELECTION); if (!docked.value) panelOpen.value = false; };
const openFlowProps = () => { designer.select(FLOW_SELECTION); panelOpen.value = true; };

function back() {
  void router.replace({ path: `/system/dynamic-modules/${moduleKey.value}`, query: { tab: 'approvalFlows', ...(!designer.server.value.isNew && meta.value?.toOptionId != null ? { target: String(meta.value.toOptionId) } : {}) } });
}

// ---- 未儲存保護（U52）：站內換頁跳自訂對話框；關分頁、重新整理只有瀏覽器原生提示 ----
const leaving = ref<((go: boolean) => void) | null>(null);
onBeforeRouteLeave(() => {
  if (!designer.dirty.value) return true;
  return new Promise<boolean>((resolve) => { leaving.value = (go) => { leaving.value = null; resolve(go); }; });
});
function onBeforeUnload(e: BeforeUnloadEvent) {
  if (!designer.dirty.value) return;
  e.preventDefault();
  e.returnValue = '';
}
onMounted(() => window.addEventListener('beforeunload', onBeforeUnload));
onBeforeUnmount(() => window.removeEventListener('beforeunload', onBeforeUnload));
</script>

<template>
  <q-page class="column no-wrap de-page" :style-fn="(offset: number, height: number) => ({ height: `${height - offset}px`, minHeight: 0 })" data-testid="flow-designer">
    <div v-if="notFound" class="q-pa-md column items-start q-gutter-y-md" data-testid="de-not-found">
      <div class="text-h6">{{ t('approval.designer.graph.notFound') }}</div>
      <q-btn flat no-caps icon="arrow_back" :label="t('approval.designer.graph.back')" data-testid="de-back" @click="back" />
    </div>
    <div v-else-if="loadFailed || flowQuery.isError.value || moduleQuery.isError.value" class="q-pa-md text-negative" data-testid="de-load-failed">
      {{ t('approval.designer.graph.loadFailed') }}
    </div>
    <div v-else-if="!designer.loaded.value" class="q-pa-md text-muted">{{ t('designer.loading') }}</div>
    <template v-else>
      <div class="row items-center no-wrap q-pa-xs de-header">
        <q-btn flat dense round icon="arrow_back" :aria-label="t('approval.designer.graph.back')" data-testid="de-back" @click="back" />
        <div class="col q-px-sm ellipsis text-subtitle1" data-testid="de-title">{{ title }}</div>
        <q-badge v-if="designer.dirty.value" color="warning" text-color="dark" class="q-mr-sm" :label="t('approval.designer.graph.unsaved')" data-testid="de-unsaved" />
        <DesignerToolbar
          :readonly="!canEdit" :phone="phone" :show-flow-props="!docked" :undoable="designer.undoable.value" :redoable="designer.redoable.value"
          @fit="canvas?.fitAll()" @flow-props="openFlowProps" @undo="designer.undo()" @redo="designer.redo()"
        />
      </div>
      <q-banner v-if="!canEdit" dense class="bg-info-soft" data-testid="de-readonly">{{ t('approval.designer.graph.readonlyBanner') }}</q-banner>

      <div class="col row no-wrap de-body">
        <div class="col de-canvas-wrap">
          <DesignerCanvas
            ref="canvas" :canvas-id="canvasId" :nodes="vf.nodes" :edges="vf.edges" :draggable="canEdit && !phone"
            @node-open="(id: string) => open({ kind: 'node', id })" @edge-open="(id: string) => open({ kind: 'edge', id })" @pane-click="onPaneClick"
            @node-move="(id: string, to: { x: number; y: number }) => designer.move(id, to)"
          />
          <aside v-if="tablet && panelOpen" class="de-drawer" :style="{ width: `${panelWidth}px` }" data-testid="de-drawer">
            <DesignerPropertyPanel :designer="designer" :selection="selection" closable @close="panelOpen = false" />
          </aside>
        </div>
        <aside v-if="docked" class="de-dock" :style="{ width: `${panelWidth}px` }" data-testid="de-dock">
          <DesignerPropertyPanel :designer="designer" :selection="selection" :closable="false" />
        </aside>
      </div>

      <q-dialog :model-value="phone && showPanel" maximized @update:model-value="panelOpen = false">
        <q-card class="column no-wrap" data-testid="de-phone-panel">
          <DesignerPropertyPanel :designer="designer" :selection="selection" closable @close="panelOpen = false" />
        </q-card>
      </q-dialog>
    </template>

    <q-dialog :model-value="leaving !== null" persistent aria-labelledby="de-leave-title">
      <q-card>
        <q-card-section id="de-leave-title" class="text-h6 q-pb-none">{{ t('approval.designer.graph.leaveTitle') }}</q-card-section>
        <q-card-section>{{ t('approval.designer.graph.leaveBody') }}</q-card-section>
        <q-card-actions align="right">
          <q-btn flat no-caps :label="t('approval.designer.graph.leaveCancel')" data-testid="de-leave-cancel" @click="leaving?.(false)" />
          <q-btn flat no-caps color="negative" :label="t('approval.designer.graph.leaveDiscard')" data-testid="de-leave-discard" @click="leaving?.(true)" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </q-page>
</template>

<style scoped>
.de-page { overflow: hidden; }
.de-header { border-bottom: 1px solid var(--t-border); }
.de-body { min-height: 0; }
.de-canvas-wrap { position: relative; min-width: 0; min-height: 0; }
.de-dock { flex: none; border-left: 1px solid var(--t-border); background: var(--t-panel); min-height: 0; overflow: hidden; }
.de-drawer {
  position: absolute; top: 0; right: 0; bottom: 0; max-width: 100%; z-index: 6; background: var(--t-panel);
  border-left: 1px solid var(--t-border); overflow: hidden;
}
</style>
