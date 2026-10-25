import { describe, expect, it } from 'vitest';
import {
  DEFAULT_ATTACHMENT_CONFIG,
  acceptAttribute,
  checkFiles,
  extensionOf,
  formatBytes,
  kindFromName,
  kindLabel,
  nameClipboardFile,
  parseAttachmentConfig,
  type AttachmentUsage,
} from './attachments';
import { attachmentErrorMessage } from './api';

const file = (name: string, size = 100) => new File([new Uint8Array(size)], name);
const noUsage: AttachmentUsage = { sentCount: 0, sentBytes: 0, pendingCount: 0, pendingBytes: 0 };
const config = { ...DEFAULT_ATTACHMENT_CONFIG, maxFileBytes: 1000, maxFilesPerMessage: 2, maxFilesPerConversation: 3, maxTotalBytesPerConversation: 2000 };

describe('parseAttachmentConfig', () => {
  it('uses the server values', () => {
    const parsed = parseAttachmentConfig({
      maxFileBytes: 5,
      maxFilesPerMessage: 2,
      maxFilesPerConversation: 3,
      maxTotalBytesPerConversation: 9,
      allowedExtensions: ['.PNG', 'pdf'],
      pasteMinChars: 100,
      pasteMinLines: 7,
    });
    expect(parsed).toEqual({
      enabled: true,
      maxFileBytes: 5,
      maxFilesPerMessage: 2,
      maxFilesPerConversation: 3,
      maxTotalBytesPerConversation: 9,
      allowedExtensions: ['.png', '.pdf'],
      pasteMinChars: 100,
      pasteMinLines: 7,
    });
  });

  it.each([null, undefined, 'x', 3, [], {}, { maxFileBytes: -1, allowedExtensions: [] }, { actionLane: 'PUBLIC_TECH' }])(
    'falls back to the defaults for %j',
    (value) => {
      expect(parseAttachmentConfig(value)).toEqual(DEFAULT_ATTACHMENT_CONFIG);
    },
  );

  it('keeps the default for a field that is missing or wrong', () => {
    const parsed = parseAttachmentConfig({ maxFilesPerMessage: 'many', pasteMinLines: 10 });
    expect(parsed.maxFilesPerMessage).toBe(DEFAULT_ATTACHMENT_CONFIG.maxFilesPerMessage);
    expect(parsed.pasteMinLines).toBe(10);
  });
});

describe('helpers', () => {
  it('reads the extension in lower case', () => {
    expect(extensionOf('Report.PDF')).toBe('.pdf');
    expect(extensionOf('archive.tar.gz')).toBe('.gz');
    expect(extensionOf('README')).toBe('');
    expect(extensionOf('.env')).toBe('');
  });

  it('builds the accept attribute from the allowed extensions', () => {
    expect(acceptAttribute({ ...config, allowedExtensions: ['.png', '.log'] })).toBe('.png,.log');
  });

  it.each([
    [0, '0 B'],
    [512, '512 B'],
    [1536, '1.5 KB'],
    [20 * 1024, '20 KB'],
    [3 * 1024 * 1024, '3.0 MB'],
    [25 * 1024 * 1024, '25 MB'],
  ])('formats %i bytes as %s', (bytes, text) => {
    expect(formatBytes(bytes)).toBe(text);
  });

  it('guesses the kind from the extension until the server says', () => {
    expect(kindFromName('shot.PNG')).toBe('image');
    expect(kindFromName('a.pdf')).toBe('pdf');
    expect(kindFromName('a.xlsx')).toBe('office');
    expect(kindFromName('a.log')).toBe('text');
    expect(kindLabel('office')).toBe('Document');
  });

  it('names a clipboard screenshot, and leaves other names alone', () => {
    const named = nameClipboardFile(new File(['x'], 'image.png', { type: 'image/png' }), new Date(2026, 9, 24, 8, 5, 9));
    expect(named.name).toBe('screenshot-20261024-080509.png');
    expect(named.type).toBe('image/png');
    const own = new File(['x'], 'my-photo.png', { type: 'image/png' });
    expect(nameClipboardFile(own, new Date())).toBe(own);
  });
});

describe('checkFiles', () => {
  it('accepts files within the limits', () => {
    const result = checkFiles([file('a.png'), file('b.log')], config, noUsage);
    expect(result.accepted.map((f) => f.name)).toEqual(['a.png', 'b.log']);
    expect(result.rejected).toEqual([]);
  });

  it('rejects an extension that is not allowed, whatever the case', () => {
    expect(checkFiles([file('virus.exe')], config, noUsage).rejected[0].reason).toMatch(/\.exe files are not supported/);
    expect(checkFiles([file('shot.PNG')], config, noUsage).accepted).toHaveLength(1);
    expect(checkFiles([file('video.mp4')], config, noUsage).accepted).toHaveLength(0);
    expect(checkFiles([file('README')], config, noUsage).rejected[0].reason).toMatch(/not supported/);
  });

  it('rejects an empty file and one over the size limit', () => {
    expect(checkFiles([file('a.txt', 0)], config, noUsage).rejected[0].reason).toMatch(/empty/);
    expect(checkFiles([file('a.txt', 1001)], config, noUsage).rejected[0].reason).toMatch(/larger than/);
    expect(checkFiles([file('a.txt', 1000)], config, noUsage).accepted).toHaveLength(1);
  });

  it('allows only so many files in one message, counting those already waiting', () => {
    const three = checkFiles([file('a.txt'), file('b.txt'), file('c.txt')], config, noUsage);
    expect(three.accepted).toHaveLength(2);
    expect(three.rejected[0].reason).toMatch(/at most 2 attachments/);

    const oneWaiting = checkFiles([file('a.txt'), file('b.txt')], config, { ...noUsage, pendingCount: 1, pendingBytes: 100 });
    expect(oneWaiting.accepted).toHaveLength(1);
  });

  it('allows only so many files in a whole conversation', () => {
    const result = checkFiles([file('a.txt')], config, { ...noUsage, sentCount: 3 });
    expect(result.accepted).toHaveLength(0);
    expect(result.rejected[0].reason).toMatch(/conversation can have at most 3/);
  });

  it('allows only so many bytes in a whole conversation', () => {
    const result = checkFiles([file('a.txt', 600)], config, { ...noUsage, sentBytes: 1500 });
    expect(result.accepted).toHaveLength(0);
    expect(result.rejected[0].reason).toMatch(/at most 2\.0 KB/);
  });

  it('reports each rejected file by name and still accepts the rest', () => {
    const result = checkFiles([file('ok.png'), file('bad.exe'), file('also-ok.csv')], config, noUsage);
    expect(result.accepted.map((f) => f.name)).toEqual(['ok.png', 'also-ok.csv']);
    expect(result.rejected).toEqual([{ name: 'bad.exe', reason: expect.stringContaining('.exe') }]);
  });
});

describe('attachmentErrorMessage', () => {
  it.each([
    ['file_too_large', 413, 'larger than the allowed size'],
    ['unsupported_type', 415, 'not supported'],
    ['too_many_files', 400, 'Too many files'],
    ['conversation_quota_exceeded', 400, 'limit for attachments'],
    ['unreadable_file', 422, 'could not be read'],
    ['rate_limited', 429, 'too quickly'],
  ])('explains %s', (code, status, text) => {
    expect(attachmentErrorMessage(status, code)).toContain(text);
  });

  it('falls back to the status when the code is missing or unknown', () => {
    expect(attachmentErrorMessage(413)).toContain('larger than the allowed size');
    expect(attachmentErrorMessage(415, 'something_new')).toContain('not supported');
    expect(attachmentErrorMessage(401)).toContain('session has expired');
    expect(attachmentErrorMessage(500)).toContain('could not be uploaded');
  });
});

describe('attachments switched off by the server', () => {
  it('reads enabled: false, and keeps the feature on when the field is absent', () => {
    expect(parseAttachmentConfig({ enabled: false }).enabled).toBe(false);
    expect(parseAttachmentConfig({ enabled: true }).enabled).toBe(true);
    expect(parseAttachmentConfig({ maxFileBytes: 2048 }).enabled).toBe(true);
  });
});
