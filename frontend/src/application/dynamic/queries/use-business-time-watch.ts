import { useQuery } from '@tanstack/vue-query';
import { Dialog } from 'quasar';
import { watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { businessOffset, setBusinessOffset } from 'src/domain/dynamic/business-time';
import { applyLocalePolicies } from 'src/i18n/locale-settings';
import { refreshLoadedMessages } from 'src/i18n/ui-messages';
import { dynamicApi } from 'src/services/api/dynamic-api';

/**
 * 別人改了營運時區：每 60 秒（與回到分頁時）重讀全系統生效值，偏移變了就先換用新值，再要求重新整理
 * （已開著的表單與列表是用舊偏移算的）。同一個新值只提示一次。與 useEffectivePolicies(null) 共用快取。
 */
export function useBusinessTimeWatch(): void {
  const composer = useI18n({ useScope: 'global' });
  const { t } = composer;
  const query = useQuery({
    queryKey: ['effective-policies', ''],
    queryFn: () => dynamicApi.getEffectivePolicies(null),
    staleTime: 60_000,
    refetchInterval: 60_000,
    refetchIntervalInBackground: false,
    refetchOnWindowFocus: true,
  });
  // 語系政策或語系資訊變了只更新設定、不提示：新設定從下一次開啟的輸入框起生效。介面文字的雜湊（uiHash）變了就背景換上新翻譯。
  // 看 dataUpdatedAt（每次成功重取都變）：結構共享會讓回應相同時資料是同一個參考，上一輪文字沒取成時要靠下一輪重試（i4b 判斷 15）；
  // 也看資料本身：同一毫秒內兩次重取時 dataUpdatedAt 不變
  watch(() => [query.dataUpdatedAt.value, query.data.value], () => {
    const eff = query.data.value;
    if (!eff?.values) return;
    applyLocalePolicies(eff.values, eff.locales);
    void refreshLoadedMessages({ global: composer }, eff.locales ?? []);
  }, { immediate: true });
  let prompted: string | null = null;
  watch(() => query.data.value?.values?.['locale.timeZone'], (zone) => {
    if (typeof zone !== 'string' || zone === businessOffset() || zone === prompted) return;
    setBusinessOffset(zone);
    prompted = zone;
    Dialog.create({
      message: t('policies.timeZoneChanged', { offset: `UTC${zone}` }),
      persistent: true,
      ok: { label: t('policies.reload') },
      cancel: false,
    }).onOk(() => window.location.reload());
  }, { immediate: true });
}
