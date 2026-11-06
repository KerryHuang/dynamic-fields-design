import { defineBoot } from '@quasar/app-vite/wrappers';
import { useThemeStore } from 'src/stores/use-theme-store';

/** 接手內嵌 script 套用的主題：同步 Quasar Dark、監聽系統與列印。需排在 pinia 之後。 */
export default defineBoot(() => {
  useThemeStore().hydrate();
});
