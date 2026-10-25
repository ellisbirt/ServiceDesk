import type { AttachmentConfig, AttachmentKind } from './api';
import { DEFAULT_PASTE_MIN_CHARS, DEFAULT_PASTE_MIN_LINES, timestampForFileName } from './pasteAsAttachment';

/** Used until the server's limits arrive, and when they cannot be read. The server enforces its own either way. */
export const DEFAULT_ATTACHMENT_CONFIG: AttachmentConfig = {
  enabled: true,
  maxFileBytes: 10 * 1024 * 1024,
  maxFilesPerMessage: 5,
  maxFilesPerConversation: 20,
  maxTotalBytesPerConversation: 25 * 1024 * 1024,
  allowedExtensions: ['.png', '.jpg', '.jpeg', '.gif', '.webp', '.pdf', '.txt', '.log', '.csv', '.json', '.md', '.docx', '.xlsx', '.pptx'],
  pasteMinChars: DEFAULT_PASTE_MIN_CHARS,
  pasteMinLines: DEFAULT_PASTE_MIN_LINES,
};

const positiveInt = (value: unknown): number | undefined =>
  typeof value === 'number' && Number.isFinite(value) && value > 0 ? Math.floor(value) : undefined;

/** The server's limits merged over the defaults. Anything missing or malformed keeps its default. */
export function parseAttachmentConfig(value: unknown): AttachmentConfig {
  const fallback = DEFAULT_ATTACHMENT_CONFIG;
  if (typeof value !== 'object' || value === null) return fallback;
  const v = value as Record<string, unknown>;
  const extensions = Array.isArray(v.allowedExtensions)
    ? v.allowedExtensions
        .filter((e): e is string => typeof e === 'string' && e.trim() !== '')
        .map((e) => (e.startsWith('.') ? e : `.${e}`).toLowerCase())
    : [];
  return {
    // Only an explicit "off" from the server switches the feature off; a missing field keeps it on.
    enabled: v.enabled !== false,
    maxFileBytes: positiveInt(v.maxFileBytes) ?? fallback.maxFileBytes,
    maxFilesPerMessage: positiveInt(v.maxFilesPerMessage) ?? fallback.maxFilesPerMessage,
    maxFilesPerConversation: positiveInt(v.maxFilesPerConversation) ?? fallback.maxFilesPerConversation,
    maxTotalBytesPerConversation: positiveInt(v.maxTotalBytesPerConversation) ?? fallback.maxTotalBytesPerConversation,
    allowedExtensions: extensions.length > 0 ? extensions : fallback.allowedExtensions,
    pasteMinChars: positiveInt(v.pasteMinChars) ?? fallback.pasteMinChars,
    pasteMinLines: positiveInt(v.pasteMinLines) ?? fallback.pasteMinLines,
  };
}

export function extensionOf(fileName: string): string {
  const dot = fileName.lastIndexOf('.');
  return dot > 0 ? fileName.slice(dot).toLowerCase() : '';
}

/** The value for an `<input type="file" accept>`. */
export function acceptAttribute(config: AttachmentConfig): string {
  return config.allowedExtensions.join(',');
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(bytes < 10 * 1024 ? 1 : 0)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(bytes < 10 * 1024 * 1024 ? 1 : 0)} MB`;
}

const KIND_LABELS: Record<AttachmentKind, string> = {
  text: 'Text',
  image: 'Image',
  pdf: 'PDF',
  office: 'Document',
};

export function kindLabel(kind: AttachmentKind): string {
  return KIND_LABELS[kind] ?? 'File';
}

/** A guess from the extension, for a file the server has not described yet. */
export function kindFromName(fileName: string): AttachmentKind {
  const extension = extensionOf(fileName);
  if (['.png', '.jpg', '.jpeg', '.gif', '.webp'].includes(extension)) return 'image';
  if (extension === '.pdf') return 'pdf';
  if (['.docx', '.xlsx', '.pptx'].includes(extension)) return 'office';
  return 'text';
}

/** A pasted screenshot arrives as "image.png"; give it a name that says what and when. */
export function nameClipboardFile(file: File, now: Date): File {
  if (!/^image\.\w+$/i.test(file.name)) return file;
  const extension = extensionOf(file.name) || '.png';
  return new File([file], `screenshot-${timestampForFileName(now)}${extension}`, { type: file.type });
}

export interface AttachmentUsage {
  /** Files already sent in this conversation, and their total size. */
  sentCount: number;
  sentBytes: number;
  /** Files already waiting to be sent with this message, and their total size. */
  pendingCount: number;
  pendingBytes: number;
}

export interface Rejection {
  name: string;
  reason: string;
}

/**
 * Checks files against the limits before any upload starts, so the user hears about a problem straight away.
 * The server checks again; this only saves a round trip.
 */
export function checkFiles(
  files: File[],
  config: AttachmentConfig,
  usage: AttachmentUsage,
): { accepted: File[]; rejected: Rejection[] } {
  const accepted: File[] = [];
  const rejected: Rejection[] = [];
  let pendingCount = usage.pendingCount;
  let bytes = usage.sentBytes + usage.pendingBytes;
  const conversationCount = () => usage.sentCount + pendingCount;

  for (const file of files) {
    const extension = extensionOf(file.name);
    const reject = (reason: string) => rejected.push({ name: file.name, reason });

    if (!config.allowedExtensions.includes(extension)) {
      reject(extension ? `${extension} files are not supported.` : 'This type of file is not supported.');
    } else if (file.size === 0) {
      reject('The file is empty.');
    } else if (file.size > config.maxFileBytes) {
      reject(`The file is larger than ${formatBytes(config.maxFileBytes)}.`);
    } else if (pendingCount >= config.maxFilesPerMessage) {
      reject(`A message can have at most ${config.maxFilesPerMessage} attachments.`);
    } else if (conversationCount() >= config.maxFilesPerConversation) {
      reject(`A conversation can have at most ${config.maxFilesPerConversation} attachments.`);
    } else if (bytes + file.size > config.maxTotalBytesPerConversation) {
      reject(`A conversation can hold at most ${formatBytes(config.maxTotalBytesPerConversation)} of attachments.`);
    } else {
      accepted.push(file);
      pendingCount += 1;
      bytes += file.size;
    }
  }
  return { accepted, rejected };
}
