import { getCurrentScope, onScopeDispose, ref, toValue, type MaybeRefOrGetter } from 'vue';

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
}

const defaultHitTest = (x: number, y: number): string | null =>
  document.elementFromPoint(x, y)?.closest('[data-board-column]')?.getAttribute('data-board-column') ?? null;

/** 看板卡片拖拉（J9）：自寫 Pointer Events，不加套件。呼叫 start(event, cardId) 開始追蹤，其餘由 window 上的監聽處理。 */
export function useCardDrag(options: DragOptions) {
  const hitTest = options.hitTest ?? defaultHitTest;
  const state = ref<DragState | null>(null);
  let teardown: (() => void) | null = null;

  function start(e: PointerEvent, cardId: number) {
    teardown?.();
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
      state.value = { cardId, x: last.x, y: last.y, overKey: hitTest(last.x, last.y) };
    };
    // 長按成立後瀏覽器還沒開始捲動，這時擋掉 touchmove 才不會一拖就捲
    const blockScroll = (ev: TouchEvent) => { if (dragging && !cancelled) ev.preventDefault(); };
    const swallowContextMenu = (ev: Event) => ev.preventDefault();

    const onMove = (ev: PointerEvent) => {
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
    const onUp = () => finish(true);
    const onCancel = () => finish(false);
    const onKey = (ev: KeyboardEvent) => {
      if (ev.key !== 'Escape' || !dragging) return;
      cancelled = true;
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
      clearTimeout(timer);
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('pointerup', onUp);
      window.removeEventListener('pointercancel', onCancel);
      window.removeEventListener('keydown', onKey);
      window.removeEventListener('touchmove', blockScroll);
      window.removeEventListener('contextmenu', swallowContextMenu, true);
      teardown = null;
    };
    window.addEventListener('pointermove', onMove);
    window.addEventListener('pointerup', onUp);
    window.addEventListener('pointercancel', onCancel);
    window.addEventListener('keydown', onKey);
    if (mode === 'longPress') {
      window.addEventListener('touchmove', blockScroll, { passive: false });
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
