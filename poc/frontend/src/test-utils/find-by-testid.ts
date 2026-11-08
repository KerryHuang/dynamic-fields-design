import { QBtnToggle, QCheckbox, QInput, QSelect, QToggle } from 'quasar';
import type { VueWrapper } from '@vue/test-utils';

type Root = Pick<VueWrapper, 'findAllComponents'>;
/* eslint-disable-next-line @typescript-eslint/no-explicit-any */
type AnyWrapper = VueWrapper<any>;

/** Quasar inputs keep `data-testid` on an inner element, so the component is found through its attrs. */
export function findInputByTestId(root: Root, testid: string): AnyWrapper {
  const all = [QSelect, QInput, QBtnToggle, QCheckbox, QToggle].flatMap((c) => root.findAllComponents(c) as AnyWrapper[]);
  return all.find((c) => c.vm.$attrs['data-testid'] === testid) as AnyWrapper;
}
