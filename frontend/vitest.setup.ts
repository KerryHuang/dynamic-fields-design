import { enableAutoUnmount } from '@vue/test-utils';
import { afterEach } from 'vitest';

// 每個測試結束就卸載掛載的元件：Quasar 的 Screen 等插件在元件存活時會排計時器，
// 若拖到該檔 jsdom 拆除後才觸發，會丟 "window is not defined" 讓整批測試算失敗（CI 偶發）。
enableAutoUnmount(afterEach);
