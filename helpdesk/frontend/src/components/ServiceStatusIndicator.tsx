import { useEffect, useState } from 'react';
import { probeServiceReadiness } from '../lib/api';

type ServiceStatus = 'checking' | 'ready' | 'unavailable';

const PROBE_INTERVAL_MS = 30_000;
const PROBE_TIMEOUT_MS = 5_000;

const statusText: Record<ServiceStatus, string> = {
  checking: 'Checking service status',
  ready: 'Service ready',
  unavailable: 'Service unavailable',
};

const statusColor: Record<ServiceStatus, string> = {
  checking: 'bg-amber-400',
  ready: 'bg-emerald-400',
  unavailable: 'bg-red-400',
};

export function ServiceStatusIndicator() {
  const [status, setStatus] = useState<ServiceStatus>('checking');

  useEffect(() => {
    let active = true;
    let probing = false;

    async function probe() {
      if (probing) return;
      probing = true;
      const controller = new AbortController();
      const timeout = window.setTimeout(() => controller.abort(), PROBE_TIMEOUT_MS);

      try {
        const ready = await probeServiceReadiness(controller.signal);
        if (active) setStatus(ready ? 'ready' : 'unavailable');
      } finally {
        window.clearTimeout(timeout);
        probing = false;
      }
    }

    void probe();
    const interval = window.setInterval(() => void probe(), PROBE_INTERVAL_MS);
    const onVisibilityChange = () => {
      if (document.visibilityState === 'visible') void probe();
    };
    document.addEventListener('visibilitychange', onVisibilityChange);

    return () => {
      active = false;
      window.clearInterval(interval);
      document.removeEventListener('visibilitychange', onVisibilityChange);
    };
  }, []);

  return (
    <span
      className="inline-flex items-center gap-2 text-xs text-zinc-400"
      role="status"
      aria-label={statusText[status]}
      title="Live check of the helpdesk API readiness endpoint"
    >
      <span aria-hidden="true" className={`h-2 w-2 rounded-full ${statusColor[status]}`} />
      <span>{statusText[status]}</span>
    </span>
  );
}
