import { onBeforeUnmount, onMounted, ref, type Ref } from 'vue';

/** 元素的內容寬度（px）；看板以容器寬度而不是視窗寬度決定一屏放幾欄（側欄開關也會改變可用寬度）。 */
export function useElementWidth(el: Ref<HTMLElement | null>) {
  const width = ref(0);
  let observer: ResizeObserver | undefined;
  onMounted(() => {
    const node = el.value;
    if (!node) return;
    width.value = node.clientWidth;
    if (typeof ResizeObserver === 'undefined') return;
    observer = new ResizeObserver((entries) => {
      const w = entries[0]?.contentRect.width;
      if (typeof w === 'number') width.value = Math.floor(w);
    });
    observer.observe(node);
  });
  onBeforeUnmount(() => observer?.disconnect());
  return width;
}
