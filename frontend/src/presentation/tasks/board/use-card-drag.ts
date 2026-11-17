import { getCurrentScope, onScopeDispose, ref, toValue, watch, type MaybeRefOrGetter, type Ref } from 'vue';

/** 拖拉的技術常數（手感，不是業務政策）：滑鼠移動超過這個距離才算拖、觸控長按多久才開始拖、長按前手指移動超過多少算在捲動。 */
export const DRAG_THRESHOLD_PX = 5;
export const LONG_PRESS_MS = 400;
export const LONG_PRESS_TOLERANCE_PX = 10;

export type DragMode = 'mouse' | 'longPress' | 'off';

/** 手機寬度（enabled=false）不拖；觸控要長按才開始，避免和捲動打架；滑鼠與觸控筆移動一小段就開始。 */
export function dragModeOf(enabled: boolean, pointerType: string): DragMode {
  if (!enabled) return 'off';
  return pointerType === 'touch' ? 'longPress' : 'mouse';
}

export interface DragState {
  cardId: number;
  x: number;
  y: number;
  /** 指標底下那一欄的欄鍵；不在任何欄上是 null。 */
  overKey: string | null;
}

export interface DragOptions {
  enabled: MaybeRefOrGetter<boolean>;
  /** 座標底下是哪一欄（欄鍵）；預設用 document.elementFromPoint 找最近的 [data-board-column]。 */
  hitTest?: (x: number, y: number) => string | null;
  /** 放在某一欄上時呼叫；沒放在任何欄上或按 Esc 取消時不呼叫。 */
  onDrop: (cardId: number, columnKey: string) => void;
  /**
   * 看板根節點。長按成立後要擋掉 touchmove 才不會一拖就捲，但瀏覽器在觸控開始時就決定這串 touchmove 能不能取消，
   * 按下之後才掛的非被動監聽可能趕不上，所以監聽常駐在這個節點上，只在拖拉中才 preventDefault。
   */
  touchRoot?: Ref<HTMLElement | null>;
}

const defaultHitTest = (x: number, y: number): string | null =>
  document.elementFromPoint(x, y)?.closest('[data-board-column]')?.getAttribute('data-board-column') ?? null;

/** 看板卡片拖拉（J9）：自寫 Pointer Events，不加套件。呼叫 start(event, cardId) 開始追蹤，其餘由 window 上的監聽處理。 */
export function useCardDrag(options: DragOptions) {
  const hitTest = options.hitTest ?? defaultHitTest;
  const state = ref<DragState | null>(null);
  let teardown: (() => void) | null = null;
  let trackedId: number | null = null;
  /** 拖拉進行中（影子已出現、沒被取消）；常駐的 touchmove 監聽看這個旗標決定要不要擋捲動。 */
  let active = false;

  const blockScroll = (ev: Event) => { if (active) ev.preventDefault(); };
  if (options.touchRoot) {
    watch(options.touchRoot, (el, _old, onCleanup) => {
      el?.addEventListener('touchmove', blockScroll, { passive: false });
      onCleanup(() => el?.removeEventListener('touchmove', blockScroll));
    }, { immediate: true, flush: 'post' });
  }

  function start(e: PointerEvent, cardId: number) {
    // 只有主要指標能開始；已經在追蹤時，其他指標一律忽略（同一個指標重新按下，代表上一次的放開沒收到，換成新的）
    if (!e.isPrimary || (teardown && e.pointerId !== trackedId)) return;
    abort();
    const pointerId = e.pointerId;
    trackedId = pointerId;
    const pointerType = e.pointerType || 'mouse';
    const mode = dragModeOf(toValue(options.enabled), pointerType);
    if (mode === 'off' || (pointerType === 'mouse' && e.button !== 0)) return;

    const origin = { x: e.clientX, y: e.clientY };
    let dragging = false;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let last = { x: origin.x, y: origin.y };

    const begin = () => {
      dragging = true;
      active = true;
      state.value = { cardId, x: last.x, y: last.y, overKey: hitTest(last.x, last.y) };
    };
    const swallowContextMenu = (ev: Event) => ev.preventDefault();

    const onMove = (ev: PointerEvent) => {
      if (ev.pointerId !== pointerId) return;
      // 滑鼠放開的事件沒收到（切到別的視窗放開）：不能讓下一次點擊把卡片放下去
      if (mode === 'mouse' && (ev.buttons & 1) === 0) {
        finish(false);
        return;
      }
      last = { x: ev.clientX, y: ev.clientY };
      const dist = Math.hypot(last.x - origin.x, last.y - origin.y);
      if (!dragging) {
        if (cancelled) return;
        if (mode === 'mouse' && dist > DRAG_THRESHOLD_PX) begin();
        else if (mode === 'longPress' && dist > LONG_PRESS_TOLERANCE_PX) finish(false);
        return;
      }
      if (!state.value) return;
      state.value = { ...state.value, x: last.x, y: last.y, overKey: hitTest(last.x, last.y) };
    };
    const onUp = (ev: PointerEvent) => { if (ev.pointerId === pointerId) finish(true); };
    const onCancel = (ev: PointerEvent) => { if (ev.pointerId === pointerId) finish(false); };
    const onBlur = () => finish(false);
    const onKey = (ev: KeyboardEvent) => {
      if (ev.key !== 'Escape' || !dragging) return;
      cancelled = true;
      active = false;
      state.value = null;
    };

    function finish(allowDrop: boolean) {
      const dropKey = allowDrop && dragging && !cancelled ? state.value?.overKey ?? null : null;
      const wasDragging = dragging;
      teardown?.();
      state.value = null;
      // 拖完放開滑鼠時瀏覽器還會補一個 click；吞掉它，卡片才不會被打開
      if (wasDragging) swallowNextClick();
      if (dropKey !== null) options.onDrop(cardId, dropKey);
    }

    teardown = () => {
      active = false;
      clearTimeout(timer);
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('pointerup', onUp);
      window.removeEventListener('pointercancel', onCancel);
      window.removeEventListener('keydown', onKey);
      window.removeEventListener('blur', onBlur);
      window.removeEventListener('contextmenu', swallowContextMenu, true);
      teardown = null;
    };
    window.addEventListener('pointermove', onMove);
    window.addEventListener('pointerup', onUp);
    window.addEventListener('pointercancel', onCancel);
    window.addEventListener('keydown', onKey);
    window.addEventListener('blur', onBlur);
    if (mode === 'longPress') {
      window.addEventListener('contextmenu', swallowContextMenu, true);
      timer = setTimeout(begin, LONG_PRESS_MS);
    }
  }

  function abort() {
    teardown?.();
    state.value = null;
  }
  if (getCurrentScope()) onScopeDispose(abort);

  return { state, start, abort };
}

function swallowNextClick() {
  const swallow = (ev: Event) => { ev.stopPropagation(); ev.preventDefault(); };
  window.addEventListener('click', swallow, true);
  setTimeout(() => window.removeEventListener('click', swallow, true), 0);
}
