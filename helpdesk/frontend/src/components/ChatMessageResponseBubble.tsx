import type { FC } from 'react';
import ReactMarkdown from 'react-markdown';
import { TICKET_PENDING_METADATA_KEY, TICKET_RAISED_METADATA_KEY, type ActionLane, type HelpdeskApiResponse } from '../lib/api';

const LANE_LABELS: Record<ActionLane, string> = {
  POLICY_VIOLATION: 'Policy',
  PUBLIC_TECH: 'General tech',
  PUBLIC_SEARCH: 'Live search',
  PRIVATE_CORP: 'Service desk',
  NO_MATCH: 'Out of scope',
};

export const ChatMessageResponseBubble: FC<{
  response: HelpdeskApiResponse;
  onStartAccessRequest?: () => void;
  accessRequestDisabled?: boolean;
  /** Passed only for the latest message: a draft the user has moved past can no longer be answered with a button. */
  onConfirmTicket?: () => void;
  onDeclineTicket?: () => void;
  ticketActionsDisabled?: boolean;
}> = ({
  response,
  onStartAccessRequest,
  accessRequestDisabled = false,
  onConfirmTicket,
  onDeclineTicket,
  ticketActionsDisabled = false,
}) => {
  const isViolation = response.actionLane === 'POLICY_VIOLATION';

  return (
    <div
      data-testid="assistant-message"
      data-lane={response.actionLane}
      className={`p-4 my-2 rounded-lg border ${
        isViolation ? 'bg-red-50/10 border-red-900/50' : 'bg-zinc-900/50 border-zinc-800'
      }`}
    >
      <span data-testid="lane-label" className="inline-block mb-2 text-[10px] uppercase tracking-wider text-zinc-400">
        {LANE_LABELS[response.actionLane] ?? response.actionLane}
      </span>

      {/* react-markdown does not render raw HTML, so model/search output cannot inject markup */}
      <div data-testid="message-body" className="text-sm font-normal leading-relaxed text-zinc-200 prose prose-invert max-w-none">
        <ReactMarkdown>{response.responseText}</ReactMarkdown>
      </div>

      {isViolation && onStartAccessRequest && (
        <button
          type="button"
          onClick={onStartAccessRequest}
          disabled={accessRequestDisabled}
          className="mt-3 px-3 py-2 rounded bg-sky-700 hover:bg-sky-600 text-xs text-white font-semibold disabled:opacity-60"
        >
          Start an access request
        </button>
      )}

      {response.metadata?.[TICKET_PENDING_METADATA_KEY] === 'true' && onConfirmTicket && onDeclineTicket && (
        <div role="group" aria-label="Send this to the service desk?" className="mt-3 flex gap-2">
          <button
            type="button"
            onClick={onConfirmTicket}
            disabled={ticketActionsDisabled}
            className="px-3 py-2 rounded bg-sky-700 hover:bg-sky-600 text-xs text-white font-semibold disabled:opacity-60"
          >
            Send to the service desk
          </button>
          <button
            type="button"
            onClick={onDeclineTicket}
            disabled={ticketActionsDisabled}
            className="px-3 py-2 rounded border border-zinc-600 hover:bg-zinc-800 text-xs text-zinc-200 disabled:opacity-60"
          >
            Don't send
          </button>
        </div>
      )}

      {response.metadata?.[TICKET_RAISED_METADATA_KEY] === 'true' && (
        <p role="status" className="mt-3 text-xs text-zinc-400">
          Sent to the service desk
        </p>
      )}
    </div>
  );
};
