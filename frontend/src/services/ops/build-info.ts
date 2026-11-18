/**
 * 前端版本：建置時由 quasar.config.ts 的 build.defineEnv 注入成 import.meta.env（與後端同一組 build-arg：APP_VERSION、GIT_COMMIT 退回 RENDER_GIT_COMMIT、BUILD_TIME）。
 * 沒注入的欄位是 dev，與後端 /api/version 一致。
 */
const orDev = (v: string | undefined) => v || 'dev';

export const buildInfo = {
  version: orDev(import.meta.env.APP_VERSION),
  commit: orDev(import.meta.env.GIT_COMMIT),
  builtAt: orDev(import.meta.env.BUILD_TIME),
} as const;

/** 錯誤回報的 version 欄位：版本號，有 commit 時接前 12 碼（Render 沒給 APP_VERSION 時仍分得出是哪一版）。 */
export const clientVersion = (info: { version: string; commit: string } = buildInfo): string =>
  info.commit === 'dev' ? info.version : `${info.version}+${info.commit.slice(0, 12)}`;
