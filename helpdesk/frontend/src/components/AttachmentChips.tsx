import type { FC } from 'react';
import { formatBytes, kindFromName, kindLabel } from '../lib/attachments';
import type { AttachmentItem } from '../lib/useAttachments';

/** The files waiting to be sent, each with its progress and a button to take it out. */
export const AttachmentChips: FC<{ items: AttachmentItem[]; onRemove: (localId: string) => void }> = ({
  items,
  onRemove,
}) => {
  if (items.length === 0) return null;

  return (
    <ul aria-label="Attachments to send" className="flex flex-wrap gap-2 mb-2">
      {items.map((item) => (
        <li
          key={item.localId}
          className={`flex items-center gap-2 px-2 py-1 text-xs rounded border ${
            item.status === 'error' ? 'border-red-900/60 bg-red-950/30' : 'border-zinc-700 bg-zinc-900'
          }`}
        >
          <span className="text-[10px] uppercase tracking-wider text-zinc-400">
            {kindLabel(item.ref?.kind ?? kindFromName(item.name))}
          </span>
          <span className="max-w-[12rem] truncate text-zinc-200" title={item.name}>
            {item.name}
          </span>
          <span className="text-zinc-400">{formatBytes(item.size)}</span>
          {item.status === 'uploading' && (
            <>
              <progress aria-label={`Uploading ${item.name}`} max={1} value={item.progress} className="w-16 h-1" />
              <span className="text-zinc-400">Uploading</span>
            </>
          )}
          {item.status === 'error' && <span className="text-red-300">{item.error}</span>}
          <button
            type="button"
            aria-label={`Remove ${item.name}`}
            onClick={() => onRemove(item.localId)}
            className="px-1 text-zinc-400 hover:text-zinc-100 rounded focus:outline-none focus:ring-1 focus:ring-indigo-500"
          >
            <span aria-hidden="true">×</span>
          </button>
        </li>
      ))}
    </ul>
  );
};
