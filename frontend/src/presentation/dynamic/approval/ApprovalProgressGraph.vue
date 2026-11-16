<script setup lang="ts">
import { QSkeleton, useQuasar } from 'quasar';
import { computed, defineAsyncComponent, defineComponent, h, ref, watch, type Ref } from 'vue';
import { useI18n } from 'vue-i18n';
import type { ApprovalActionView, ApprovalProgressGraph, ApprovalStageView } from 'src/domain/dynamic/approval-types';
import type { ModuleSchema } from 'src/domain/dynamic/types';
import { buildProgressGraph, graphHeight, initialViewport, GRAPH_MIN_HEIGHT, THUMB_HEIGHT } from './progress-graph';
import ProgressNodeDetail from './ProgressNodeDetail.vue';

// 簽核進度圖。寬螢幕（≥ 600）直接在區塊裡互動；手機（< 600）是 180px 的縮圖（整張縮進來、不能互動、不攔頁面捲動），
// 點縮圖或「放大檢視」開全螢幕，在裡面平移縮放。點簽核節點與加簽小節點開細節（U4、U6）。
// 外層只算排版、圖高與視角，畫布與 Vue Flow 在 ProgressGraphCanvas 動態載入，所以排版與對照留在詳情頁的塊裡、
// 載入前就知道高度，骨架與畫布一樣高、不會跳動（fg4 計畫 J3）。
const props = defineProps<{ graph: ApprovalProgressGraph; stages: ApprovalStageView[]; actions: ApprovalActionView[]; schema: ModuleSchema | null; instanceId: number }>();
const { t, locale } = useI18n();
const $q = useQuasar();

const phone = computed(() => $q.screen.lt.sm);
const mode = computed(() => (phone.value ? 'thumb' : 'inline'));
const built = computed(() => buildProgressGraph(props.graph, props.stages, { locale: locale.value, t }));
const maxHeight = computed(() => Math.max(GRAPH_MIN_HEIGHT, $q.screen.height * 0.6));

// 載入中的骨架不吃外層傳給畫布的屬性（width、height 是數字，QSkeleton 要字串）。
const Skeleton = defineComponent({ inheritAttrs: false, render: () => h(QSkeleton, { square: true, height: '100%', width: '100%' }) });
const Canvas = defineAsyncComponent({ loader: () => import('./ProgressGraphCanvas.vue'), loadingComponent: Skeleton, delay: 0 });

/** 量元素內側尺寸，隨視窗或版面改變重新算視角與高度。 */
function watchSize(el: Ref<HTMLElement | null>, onSize: (w: number, h: number) => void) {
  watch(el, (e, _old, onCleanup) => {
    if (!e) return;
    const measure = () => onSize(e.clientWidth, e.clientHeight);
    measure();
    if (typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(measure);
    observer.observe(e);
    onCleanup(() => observer.disconnect());
  }, { flush: 'post' });
}

// 區塊裡的圖（寬螢幕是互動畫布，手機是縮圖）
const frame = ref<HTMLElement | null>(null);
const width = ref(0);
watchSize(frame, (w) => { width.value = w; });
const plan = computed(() => (width.value > 0 ? initialViewport(built.value.layout, width.value, mode.value, { focus: built.value.focus, maxHeight: maxHeight.value }) : null));
const height = computed(() => (width.value > 0 ? graphHeight(built.value.layout, width.value, mode.value, $q.screen.height) : phone.value ? THUMB_HEIGHT : GRAPH_MIN_HEIGHT));

// 手機的全螢幕檢視：對話框顯示完（@show）之後才量尺寸、套初始視角，開啟動畫中容器還沒有尺寸。
const fullOpen = ref(false);
const fullShown = ref(false);
const fullFrame = ref<HTMLElement | null>(null);
const fullSize = ref({ w: 0, h: 0 });
watchSize(fullFrame, (w, h) => { fullSize.value = { w, h }; });
const fullPlan = computed(() => (fullShown.value && fullSize.value.w > 0 && fullSize.value.h > 0
  ? initialViewport(built.value.layout, fullSize.value.w, 'full', { focus: built.value.focus, maxHeight: fullSize.value.h })
  : null));

// 節點細節
const detail = ref<{ name: string; state: string; runIds: number[] }>({ name: '', state: '', runIds: [] });
const detailOpen = ref(false);
function openNode(id: string) {
  const d = built.value.nodes.find((n) => n.id === id)?.data;
  if (!d?.clickable) return;
  detail.value = { name: d.label, state: d.statusText, runIds: d.runIds };
  detailOpen.value = true;
}
</script>

<template>
  <div class="approval-progress-graph q-mb-md" data-testid="approval-progress-graph">
    <div ref="frame" class="pg-frame" :style="{ height: `${height}px` }">
      <Canvas
        v-if="plan" :key="mode" :instance-id="instanceId" :mode="mode" :nodes="built.nodes" :edges="built.edges" :plan="plan" :width="width" :height="height"
        @node-open="openNode"
      />
      <QSkeleton v-else square height="100%" width="100%" data-testid="pg-skeleton" />
      <template v-if="phone">
        <!-- 縮圖整張是一個點擊區（蓋在畫布上，所以畫布收不到任何手勢，頁面照常捲動）；鍵盤與讀屏用右上的按鈕。 -->
        <div class="pg-thumb-hit" aria-hidden="true" data-testid="pg-thumb" @click="fullOpen = true" />
        <q-btn
          class="pg-expand" dense unelevated no-caps size="sm" icon="open_in_full" :label="t('approval.graph.expand')" data-testid="pg-expand"
          @click="fullOpen = true"
        />
      </template>
    </div>

    <q-dialog v-model="fullOpen" maximized :aria-label="t('approval.graph.label')" @show="fullShown = true" @hide="fullShown = false">
      <q-card class="pg-full column no-wrap" data-testid="pg-full">
        <q-card-section class="row items-center no-wrap q-py-sm">
          <div class="text-subtitle1 col">{{ t('approval.graph.label') }}</div>
          <q-btn flat round dense icon="close" :aria-label="t('approval.graph.close')" data-testid="pg-full-close" @click="fullOpen = false" />
        </q-card-section>
        <div ref="fullFrame" class="pg-full-frame col" data-testid="pg-full-frame">
          <Canvas
            v-if="fullPlan" :instance-id="instanceId" mode="full" :nodes="built.nodes" :edges="built.edges" :plan="fullPlan" :width="fullSize.w" :height="fullSize.h"
            @node-open="openNode"
          />
        </div>
      </q-card>
    </q-dialog>

    <ProgressNodeDetail
      v-model="detailOpen" :name="detail.name" :state="detail.state" :run-ids="detail.runIds" :stages="stages" :actions="actions" :schema="schema" :bottom="phone"
    />
  </div>
</template>

<style scoped>
.pg-frame { position: relative; box-sizing: content-box; border: 1px solid var(--t-border); border-radius: var(--t-radius); background: var(--t-panel); overflow: hidden; }
.pg-thumb-hit { position: absolute; inset: 0; z-index: 4; cursor: pointer; -webkit-tap-highlight-color: transparent; }
.pg-expand { position: absolute; top: 4px; right: 4px; z-index: 5; background: var(--t-panel); color: var(--t-text); border: 1px solid var(--t-border); }
.pg-full { background: var(--t-panel); color: var(--t-text); }
.pg-full-frame { min-height: 0; position: relative; overflow: hidden; border-top: 1px solid var(--t-border); }
</style>
