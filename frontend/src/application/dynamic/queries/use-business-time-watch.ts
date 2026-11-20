import { useQuery } from '@tanstack/vue-query';
import { Dialog } from 'quasar';
import { watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { businessOffset, setBusinessOffset } from 'src/domain/dynamic/business-time';
import { applyLocalePolicies } from 'src/i18n/locale-settings';
import { dynamicApi } from 'src/services/api/dynamic-api';

/**
 * 別人改了營運時區：每 60 秒（與回到分頁時）重讀全系統生效值，偏移變了就先換用新值，再要求重新整理
 * （已開著的表單與列表是用舊偏移算的）。同一個新值只提示一次。與 useEffectivePolicies(null) 共用快取。
 */
export function useBusinessTimeWatch(): void {
  const { t } = useI18n();
  const query = useQuery({
    queryKey: ['effective-policies', ''],
    queryFn: () => dynamicApi.getEffectivePolicies(null),
    staleTime: 60_000,
    refetchInterval: 60_000,
    refetchIntervalInBackground: false,
    refetchOnWindowFocus: true,
  });
  // 語系政策變了只更新設定、不提示：新設定從下一次開啟的輸入框起生效
  watch(() => query.data.value?.values, (values) => { if (values) applyLocalePolicies(values); }, { immediate: true });
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
