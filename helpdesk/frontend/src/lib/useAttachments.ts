import { useEffect, useRef, useState } from 'react';
import {
  AttachmentUploadError,
  attachmentErrorMessage,
  deleteAttachment,
  uploadAttachment,
  type AttachmentRef,
} from './api';

/** One file in the composer: uploading, ready to send, or failed. */
export interface AttachmentItem {
  localId: string;
  name: string;
  size: number;
  status: 'uploading' | 'done' | 'error';
  /** 0 to 1 while uploading. */
  progress: number;
  /** Set once the server has stored the file. */
  ref?: AttachmentRef;
  error?: string;
}

/**
 * The files waiting to be sent with the next message. Each starts uploading as soon as it is added, so by the time the
 * user presses Send they are normally ready. Leaving the conversation abandons them.
 */
export function useAttachments(conversationId: string, authHeaders: () => Promise<Record<string, string>>) {
  const [items, setItems] = useState<AttachmentItem[]>([]);
  const controllers = useRef(new Map<string, AbortController>());

  useEffect(() => {
    const running = controllers.current;
    return () => {
      running.forEach((controller) => controller.abort());
      running.clear();
    };
  }, []);

  function patch(localId: string, change: Partial<AttachmentItem>) {
    setItems((prev) => prev.map((item) => (item.localId === localId ? { ...item, ...change } : item)));
  }

  /** Adds a file and starts uploading it. Returns its local id. The caller has already checked it against the limits. */
  function start(file: File): string {
    const localId = crypto.randomUUID();
    const controller = new AbortController();
    controllers.current.set(localId, controller);
    setItems((prev) => [...prev, { localId, name: file.name, size: file.size, status: 'uploading', progress: 0 }]);

    uploadAttachment(file, conversationId, authHeaders, (progress) => patch(localId, { progress }), controller.signal)
      .then((ref) => patch(localId, { status: 'done', progress: 1, ref }))
      .catch((err: unknown) => {
        if (controller.signal.aborted) return;
        patch(localId, {
          status: 'error',
          error: err instanceof AttachmentUploadError ? err.message : attachmentErrorMessage(0),
        });
      })
      .finally(() => controllers.current.delete(localId));
    return localId;
  }

  /** Takes a file out. A file still uploading is cancelled; one already stored is deleted on the server. */
  function remove(localId: string) {
    const item = items.find((i) => i.localId === localId);
    controllers.current.get(localId)?.abort();
    if (item?.ref) void deleteAttachment(item.ref.id, conversationId, authHeaders);
    setItems((prev) => prev.filter((i) => i.localId !== localId));
  }

  /** Forgets the files once they have been sent: they now belong to the message. */
  function clear() {
    setItems([]);
  }

  /** Abandons everything for the current conversation (call before switching to a new one). */
  function reset() {
    controllers.current.forEach((controller) => controller.abort());
    controllers.current.clear();
    for (const item of items) {
      if (item.ref) void deleteAttachment(item.ref.id, conversationId, authHeaders);
    }
    setItems([]);
  }

  return {
    items,
    start,
    remove,
    clear,
    reset,
    /** The stored files, ready to send. */
    refs: items.flatMap((item) => (item.status === 'done' && item.ref ? [item.ref] : [])),
    uploading: items.some((item) => item.status === 'uploading'),
  };
}
