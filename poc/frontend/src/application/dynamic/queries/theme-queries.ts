import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { computed, watch } from 'vue';
import { useRoute } from 'vue-router';
import type { Me } from 'src/domain/dynamic/types';
import type { MeTheme, ThemeChoice, ThemeDefaults } from 'src/domain/theme/theme-model';
import { dynamicApi } from 'src/services/api/dynamic-api';
import type { ApiError } from 'src/services/api/http';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { useThemeStore } from 'src/stores/use-theme-store';

const MY_THEME = ['my-theme'];

/**
 * 讓 <html> 的主題跟著登入狀態：已登入用 /me（與 useMe 共用快取）；未登入且在 public 路由用 GET /theme。
 * Quasar SPA 在 auth guard 跑完前就會 mount，起始路由沒有 meta.public，所以 guard 還在等 /me 時兩個查詢都不會送。
 */
export function useThemeSync() {
  const identity = useIdentityStore();
  const theme = useThemeStore();
  const route = useRoute();
  const qc = useQueryClient();

  const loggedIn = computed(() => !!identity.userId);
  const me = useQuery({ queryKey: computed(() => ['me', identity.userId]), queryFn: dynamicApi.getMe, enabled: loggedIn });
  watch(
    () => me.data.value,
    (d) => {
      // 正在送儲存時略過：在途的舊資料會蓋掉剛選的值；儲存結束時會自己寫回 me.theme。
      if (d && loggedIn.value && qc.isMutating({ mutationKey: MY_THEME }) === 0) theme.applyMe(d.theme);
    },
    { immediate: true },
  );

  const anonymous = computed(() => !identity.userId && route.meta.public === true);
  const defaults = useQuery({ queryKey: ['theme-defaults'], queryFn: dynamicApi.getThemeDefaults, enabled: anonymous });
  // 失敗什麼都不做：留在快取或出廠值。同時監看 anonymous，登出後即使資料沒變也重新套用。
  watch(
    [() => defaults.data.value, anonymous],
    ([d, anon]) => { if (d && anon) theme.applyAnonymous(d); },
    { immediate: true },
  );
}

interface SelectVars { choice: ThemeChoice; onError: () => void }

/**
 * 個人選擇外觀：立刻生效，背景依序儲存。Demo 站（canSave 為 false）只寫瀏覽器。
 * 連選多次時只有佇列最後一個請求結束才收尾：成功以伺服器回應為準，失敗回到最後一次確定成功的設定。
 */
export function useSelectTheme(): (choice: ThemeChoice, onError: () => void) => void {
  const theme = useThemeStore();
  const identity = useIdentityStore();
  const qc = useQueryClient();
  let confirmed: ThemeChoice | null = null;

  const m = useMutation<MeTheme, ApiError, SelectVars>({
    mutationKey: MY_THEME,
    scope: { id: 'my-theme' },
    mutationFn: (v) => dynamicApi.saveMyTheme(v.choice),
    onMutate: async (v) => {
      confirmed ??= { ...theme.choice };
      await qc.cancelQueries({ queryKey: ['me', identity.userId] });
      theme.setChoice(v.choice);
    },
    onSuccess: (res) => { confirmed = { mode: res.mode, style: res.style }; },
    onError: (_e, v) => v.onError(),
    // onSettled 執行時這個 mutation 仍是 pending，所以「最後一個」是 isMutating === 1
    onSettled: (res, error) => {
      if (qc.isMutating({ mutationKey: MY_THEME }) !== 1) return;
      if (error) {
        if (confirmed) theme.setChoice(confirmed);
      } else if (res) {
        qc.setQueryData<Me>(['me', identity.userId], (old) => (old ? { ...old, theme: res } : old));
      }
      confirmed = null;
    },
  });

  return (choice, onError) => {
    if (!theme.canSave) {
      theme.setChoice(choice);
      return;
    }
    m.mutate({ choice, onError });
  };
}

/** 管理員存全站預設；其他已開著的畫面不推送，下次取得 /me 時生效。 */
export function useSaveSiteTheme() {
  const qc = useQueryClient();
  return useMutation<ThemeDefaults, ApiError, ThemeDefaults>({
    mutationFn: (d) => dynamicApi.saveSiteTheme(d),
    onSuccess: (res) => {
      // 先用回應更新已確認的值：之後重新取得 /me 即使失敗，畫面與失敗還原都不會停在舊設定
      qc.setQueriesData<Me>({ queryKey: ['me'] }, (old) => old && {
        ...old,
        theme: {
          ...old.theme, defaultMode: res.mode, defaultStyle: res.style,
          effectiveMode: old.theme.mode ?? res.mode, effectiveStyle: old.theme.style ?? res.style,
        },
      });
      return qc.invalidateQueries({ queryKey: ['me'] });
    },
  });
}
