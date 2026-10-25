import { useEffect, useState } from 'react';

export function QueueErrorBubble({
  text,
  availableAt,
  disabled,
  onRetry,
}: {
  text: string;
  availableAt?: number;
  disabled: boolean;
  onRetry: () => void;
}) {
  const [now, setNow] = useState(Date.now);
  const seconds = availableAt === undefined ? 0 : Math.max(0, Math.ceil((availableAt - now) / 1000));

  useEffect(() => {
    if (availableAt === undefined) return;
    const timer = window.setInterval(() => {
      const time = Date.now();
      setNow(time);
      if (time >= availableAt) window.clearInterval(timer);
    }, 250);
    return () => window.clearInterval(timer);
  }, [availableAt]);

  return (
    <div role="alert" className="p-3 my-2 rounded-lg border border-red-900/50 bg-red-950/30 text-sm text-red-300">
      <p>{text}</p>
      {availableAt !== undefined && (
        <button
          type="button"
          disabled={disabled || seconds > 0}
          onClick={onRetry}
          className="mt-2 px-3 py-1 rounded bg-red-900 hover:bg-red-800 disabled:opacity-40 transition-colors"
        >
          {seconds > 0 ? `Retry in ${seconds}s` : 'Retry'}
        </button>
      )}
    </div>
  );
}
