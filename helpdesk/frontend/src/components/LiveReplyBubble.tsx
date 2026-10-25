import type { FC } from 'react';
import ReactMarkdown from 'react-markdown';

export interface LiveReplyBubbleProps {
  /** Reply text received so far. */
  text: string;
  /** Set while the AI service is rate limited and the request is waiting to be retried. */
  notice?: string;
  /** Seconds since the server started working, from its heartbeat. */
  elapsedSeconds?: number;
  stopped?: boolean;
}

const SLOW_AFTER_SECONDS = 10;

function statusLine({ text, notice, elapsedSeconds = 0 }: LiveReplyBubbleProps): string {
  if (notice) return notice;
  if (elapsedSeconds >= SLOW_AFTER_SECONDS) return `Still working… ${Math.floor(elapsedSeconds)}s`;
  return text ? '' : 'Assistant is thinking…';
}

/** The assistant's reply while it is still arriving. Replaced by the full response bubble when it completes. */
export const LiveReplyBubble: FC<LiveReplyBubbleProps> = (props) => {
  const status = statusLine(props);

  // aria-live is off here: announcing every streamed fragment would be unusable. The finished bubble is announced.
  return (
    <div
      data-testid={props.stopped ? 'stopped-reply' : 'live-reply'}
      aria-live={props.stopped ? 'polite' : 'off'}
      aria-busy={!props.stopped}
      className="p-4 my-2 rounded-lg border bg-zinc-900/50 border-zinc-800"
    >
      {props.text && (
        <div className="text-sm font-normal leading-relaxed text-zinc-200 prose prose-invert max-w-none">
          <ReactMarkdown>{props.text}</ReactMarkdown>
        </div>
      )}
      {status && (
        <p role="status" className="mt-1 text-xs text-zinc-400">
          {status}
        </p>
      )}
    </div>
  );
};
