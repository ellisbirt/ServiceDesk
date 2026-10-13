import { QueryClient, QueryClientProvider, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState, useSyncExternalStore, type ReactNode } from 'react';
import { BrowserRouter, NavLink, Navigate, Route, Routes } from 'react-router';
import { ApiError } from './lib/api';
import { isDevAuth, isEntraConfigured, isSignInRequired, onSignInRequiredChange, signIn } from './lib/auth';
import { connectTicketsHub, type HubState } from './lib/hub';
import { useMe } from './lib/queries';
import { TicketListPage } from './pages/TicketListPage';
import { TicketDetailPage } from './pages/TicketDetailPage';
import { NewTicketPage } from './pages/NewTicketPage';
import { KbListPage } from './pages/KbListPage';
import { KbEditorPage } from './pages/KbEditorPage';

export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 15_000,
        refetchOnWindowFocus: false,
        // Client errors (404, 403, 412) will not fix themselves; only retry transient failures.
        retry: (count, err) => count < 2 && !(err instanceof ApiError && err.status >= 400 && err.status < 500),
      },
    },
  });
}

const HUB_LABELS: Record<HubState, string> = {
  connecting: 'Connecting…',
  connected: 'Live',
  reconnecting: 'Reconnecting…',
  disconnected: 'Offline',
};

function LiveIndicator({ enabled }: { enabled: boolean }) {
  const queryClient = useQueryClient();
  const [state, setState] = useState<HubState>('connecting');
  useEffect(() => {
    if (!enabled) return;
    return connectTicketsHub(queryClient, isEntraConfigured, setState);
  }, [enabled, queryClient]);
  if (!enabled) return null;
  const dot = state === 'connected' ? 'bg-green-600' : state === 'disconnected' ? 'bg-red-600' : 'bg-amber-500';
  return (
    <span className="flex items-center gap-1.5 text-xs text-slate-200" role="status" title="Live ticket updates">
      <span aria-hidden="true" className={`h-2 w-2 rounded-full ${dot}`} />
      {HUB_LABELS[state]}
    </span>
  );
}

const navClass = ({ isActive }: { isActive: boolean }) =>
  `rounded-md px-3 py-1.5 text-sm font-medium ${isActive ? 'bg-slate-700 text-white' : 'text-slate-200 hover:bg-slate-800'}`;

/** The routed console. Rendered inside a router and a QueryClientProvider (see App, and the tests). */
export function AppShell({ live = true }: { live?: boolean }) {
  const signInRequired = useSyncExternalStore(onSignInRequiredChange, isSignInRequired);
  const me = useMe();
  return (
    <div className="flex min-h-screen flex-col">
      <a href="#main" className="sr-only focus:not-sr-only focus:absolute focus:left-2 focus:top-2 focus:bg-white focus:p-2">
        Skip to content
      </a>
      <header className="bg-slate-900 text-white">
        <div className="mx-auto flex max-w-7xl items-center gap-4 px-4 py-2">
          <span className="text-lg font-semibold">AgentDesk</span>
          <nav aria-label="Main" className="flex gap-1">
            <NavLink to="/tickets" className={navClass}>
              Tickets
            </NavLink>
            <NavLink to="/kb" className={navClass}>
              Knowledge base
            </NavLink>
          </nav>
          <div className="ml-auto flex items-center gap-4">
            <LiveIndicator enabled={live && !signInRequired} />
            {me.data && (
              <span className="text-sm text-slate-200">
                {me.data.displayName ?? me.data.userId}
                {isDevAuth && <span className="ml-1 text-xs text-amber-300">(dev)</span>}
              </span>
            )}
          </div>
        </div>
      </header>

      {signInRequired && (
        <div role="alert" className="flex items-center justify-between gap-4 border-b border-amber-300 bg-amber-50 px-4 py-2 text-sm text-amber-900">
          <span>Your session has expired. Sign in again to continue.</span>
          <button type="button" onClick={() => void signIn()} className="btn-secondary">
            Sign in
          </button>
        </div>
      )}

      <main id="main" className="mx-auto w-full max-w-7xl flex-1 px-4 py-4">
        <Routes>
          <Route path="/" element={<Navigate to="/tickets" replace />} />
          <Route path="/tickets" element={<TicketListPage />} />
          <Route path="/tickets/new" element={<NewTicketPage />} />
          <Route path="/tickets/:id" element={<TicketDetailPage />} />
          <Route path="/kb" element={<KbListPage />} />
          <Route path="/kb/new" element={<KbEditorPage />} />
          <Route path="/kb/:id" element={<KbEditorPage />} />
          <Route path="*" element={<NotFound />} />
        </Routes>
      </main>
    </div>
  );
}

function NotFound() {
  return (
    <div className="card">
      <h1 className="text-lg font-semibold">Page not found</h1>
      <NavLink to="/tickets" className="text-blue-800 underline">
        Back to tickets
      </NavLink>
    </div>
  );
}

export function Providers({ children, client }: { children: ReactNode; client?: QueryClient }) {
  const [queryClient] = useState(() => client ?? createQueryClient());
  return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
}

export default function App() {
  return (
    <Providers>
      <BrowserRouter>
        <AppShell />
      </BrowserRouter>
    </Providers>
  );
}
