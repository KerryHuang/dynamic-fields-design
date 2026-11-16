import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ref } from 'vue';
import { createI18n } from 'vue-i18n';
import type { AttachmentSettings } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { attachmentContextKey, type AttachmentContext } from 'src/presentation/dynamic/attachment-context';
import AttachmentInput from './AttachmentInput.vue';

const settings = ref<AttachmentSettings | undefined>();
vi.mock('src/application/dynamic/queries/attachment-queries', () => ({ useAttachmentSettings: () => ({ data: settings }) }));
const api = vi.hoisted(() => ({ uploadAttachment: vi.fn(), downloadAttachment: vi.fn() }));
vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: api }));

const file = (id: string, name: string, size = 2048) => ({ id: id.repeat(32), name, size });
const ON: AttachmentSettings = { uploadEnabled: true, maxBytes: 10485760, allowedExtensions: ['pdf', 'png'], maxFilesPerField: 20 };

function mountInput(props: Record<string, unknown> = {}, context?: AttachmentContext) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(AttachmentInput, {
    props: { label: '附件', fieldId: 7, modelValue: [file('a', '報價.pdf')], ...props },
    global: {
      plugins: [[quasar.Quasar, { components: quasar }], i18n],
      provide: context ? { [attachmentContextKey as symbol]: context } : {},
    },
  });
}

describe('AttachmentInput', () => {
  beforeEach(() => {
    settings.value = ON;
    api.uploadAttachment.mockReset();
    api.downloadAttachment.mockReset();
  });

  it('lists file names and removing one emits the remaining files', async () => {
    const w = mountInput({ modelValue: [file('a', '報價.pdf'), file('b', '合約.png')] });
    expect(w.findAll('[data-testid="attachment-download"]').map((a) => a.text())).toEqual(['報價.pdf', '合約.png']);
    await w.findAll('[data-testid="attachment-remove"]')[0]!.trigger('click');
    expect(w.emitted('update:modelValue')![0]).toEqual([[file('b', '合約.png')]]);
  });

  it('read-only shows no remove and no upload controls', () => {
    const w = mountInput({ readonly: true }, { recordId: 5, onUploaded: vi.fn() });
    expect(w.find('[data-testid="attachment-remove"]').exists()).toBe(false);
    expect(w.find('[data-testid="attachment-upload"]').exists()).toBe(false);
  });

  it('without a saved record asks to save first instead of offering upload', () => {
    const w = mountInput({}, { recordId: null, onUploaded: vi.fn() });
    expect(w.find('[data-testid="attachment-upload"]').exists()).toBe(false);
    expect(w.text()).toContain(zhTW.attachment.saveFirst);
  });

  it('uploading appends the server file, passes the new rowVersion to the form and sends the picked file', async () => {
    const onUploaded = vi.fn();
    api.uploadAttachment.mockResolvedValue({ ...file('c', '新.pdf'), rowVersion: 'rv2' });
    const w = mountInput({}, { recordId: 5, onUploaded });
    const picked = new File(['x'], '新.pdf');
    const input = w.find('[data-testid="attachment-file"]');
    Object.defineProperty(input.element, 'files', { value: [picked] });
    await input.trigger('change');
    await flushPromises();
    expect(api.uploadAttachment).toHaveBeenCalledWith(5, 7, picked);
    expect(onUploaded).toHaveBeenCalledWith('rv2');
    expect(w.emitted('update:modelValue')![0]).toEqual([[file('a', '報價.pdf'), file('c', '新.pdf')]]);
  });

  it('shows the operator reason and hides upload when uploads are off', () => {
    settings.value = { ...ON, uploadEnabled: false, disabledReason: '展示站磁碟會清空' };
    const w = mountInput({}, { recordId: 5, onUploaded: vi.fn() });
    expect(w.find('[data-testid="attachment-upload"]').exists()).toBe(false);
    expect(w.get('[data-testid="attachment-upload-off"]').text()).toBe('展示站磁碟會清空');
  });

  it('falls back to the localized default reason when none is configured', () => {
    settings.value = { ...ON, uploadEnabled: false };
    const w = mountInput({}, { recordId: 5, onUploaded: vi.fn() });
    expect(w.get('[data-testid="attachment-upload-off"]').text()).toBe(zhTW.attachment.uploadOffDefault);
  });

  it('existing files stay downloadable when uploads are off', async () => {
    settings.value = { ...ON, uploadEnabled: false };
    api.downloadAttachment.mockResolvedValue(new Blob(['x']));
    vi.stubGlobal('URL', { ...URL, createObjectURL: () => 'blob:x', revokeObjectURL: () => {} });
    const w = mountInput({ readonly: true, recordId: 5 });
    await w.get('[data-testid="attachment-download"]').trigger('click');
    await flushPromises();
    expect(api.downloadAttachment).toHaveBeenCalledWith(5, 7, 'a'.repeat(32));
  });
});
