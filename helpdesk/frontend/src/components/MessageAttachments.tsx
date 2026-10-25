import type { FC } from 'react';
import type { AttachmentRef } from '../lib/api';
import { formatBytes, kindLabel } from '../lib/attachments';

/** The files that were sent with a message, shown under it. */
export const MessageAttachments: FC<{ attachments: AttachmentRef[] }> = ({ attachments }) => {
  if (attachments.length === 0) return null;

  return (
    <ul aria-label="Attachments" className="mt-2 space-y-1">
      {attachments.map((attachment) => (
        <li key={attachment.id} className="flex items-center gap-2 text-xs text-white">
          <span className="text-[10px] uppercase tracking-wider text-indigo-50">{kindLabel(attachment.kind)}</span>
          <span className="truncate" title={attachment.name}>
            {attachment.name}
          </span>
          <span className="text-indigo-50">{formatBytes(attachment.size)}</span>
        </li>
      ))}
    </ul>
  );
};
