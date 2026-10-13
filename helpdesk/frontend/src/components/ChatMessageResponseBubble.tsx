import type { FC } from 'react';
import ReactMarkdown from 'react-markdown';
import { ESCALATION_TOKEN_METADATA_KEY, type ActionLane, type HelpdeskApiResponse } from '../lib/api';
import { IntakeFormCard } from './IntakeFormCard';

const LANE_LABELS: Record<ActionLane, string> = {
  POLICY_VIOLATION: 'Policy',
  PUBLIC_TECH: 'General tech',
  PUBLIC_SEARCH: 'Live search',
  PRIVATE_CORP: 'Escalated',
  NO_MATCH: 'Out of scope',
};

export const ChatMessageResponseBubble: FC<{ response: HelpdeskApiResponse }> = ({ response }) => {
  const isViolation = response.actionLane === 'POLICY_VIOLATION';

  return (
    <div
      className={`p-4 my-2 rounded-lg border ${
        isViolation ? 'bg-red-50/10 border-red-900/50' : 'bg-zinc-900/50 border-zinc-800'
      }`}
    >
      <span className="inline-block mb-2 text-[10px] uppercase tracking-wider text-zinc-400">
        {LANE_LABELS[response.actionLane] ?? response.actionLane}
      </span>

      {/* react-markdown does not render raw HTML, so model/search output cannot inject markup */}
      <div className="text-sm font-normal leading-relaxed text-zinc-200 prose prose-invert max-w-none">
        <ReactMarkdown>{response.responseText}</ReactMarkdown>
      </div>

      {response.intakeForm && response.metadata?.[ESCALATION_TOKEN_METADATA_KEY] && (
        <IntakeFormCard
          form={response.intakeForm}
          conversationId={response.conversationId}
          escalationToken={response.metadata[ESCALATION_TOKEN_METADATA_KEY]}
        />
      )}
    </div>
  );
};
