import { useInfiniteQuery } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { Link, useSearchParams } from 'react-router';
import { api } from '../lib/api';
import { humanize, relativeTime, statusLabel } from '../lib/format';
import { keys, useVocabulary } from '../lib/queries';
import type { TicketQuery } from '../lib/types';
import { ErrorAlert, PriorityBadge, SlaBadge, Spinner, StatusBadge } from '../components/ui';

const ACTIVE = 'new,open,pending,on_hold';

export const VIEWS = {
  mine: { label: 'My open tickets', query: { assignee: 'me', status: ACTIVE } },
  unassigned: { label: 'Unassigned', query: { unassigned: true, status: ACTIVE } },
  breached: { label: 'Breaching SLA', query: { breached: true } },
  open: { label: 'All open', query: { status: ACTIVE } },
  all: { label: 'All tickets', query: {} },
} as const satisfies Record<string, { label: string; query: TicketQuery }>;

type ViewKey = keyof typeof VIEWS;

const SORTS = [
  ['updatedAt', 'Last updated'],
  ['createdAt', 'Newest'],
  ['priority', 'Priority'],
  ['dueAt', 'SLA due'],
] as const;

export function TicketListPage() {
  const [params, setParams] = useSearchParams();
  const vocab = useVocabulary();
  const view = (params.get('view') as ViewKey) in VIEWS ? (params.get('view') as ViewKey) : 'open';
  const filters: TicketQuery = {
    ...VIEWS[view].query,
    ...(params.get('status') ? { status: params.get('status')! } : {}),
    priority: params.get('priority') ?? undefined,
    category: params.get('category') ?? undefined,
    q: params.get('q') ?? undefined,
    sort: params.get('sort') ?? undefined,
  };
  const [search, setSearch] = useState(params.get('q') ?? '');

  const list = useInfiniteQuery({
    queryKey: keys.ticketList(filters),
    queryFn: ({ pageParam, signal }) => api.listTickets({ ...filters, continuationToken: pageParam }, signal),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.continuationToken,
  });

  const update = (changes: Record<string, string | undefined>) => {
    const next = new URLSearchParams(params);
    for (const [k, v] of Object.entries(changes)) {
      if (v) next.set(k, v);
      else next.delete(k);
    }
    setParams(next, { replace: true });
  };

  const onSearch = (e: FormEvent) => {
    e.preventDefault();
    update({ q: search.trim() || undefined });
  };

  const tickets = list.data?.pages.flatMap((p) => p.items) ?? [];

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h1 className="text-xl font-semibold text-slate-900">Tickets</h1>
        <Link to="/tickets/new" className="btn-primary">
          New ticket
        </Link>
      </div>

      <nav aria-label="Saved views" className="flex flex-wrap gap-1">
        {(Object.keys(VIEWS) as ViewKey[]).map((key) => (
          <button
            key={key}
            type="button"
            aria-pressed={view === key}
            onClick={() => update({ view: key, status: undefined })}
            className={`rounded-full px-3 py-1 text-sm ring-1 ring-inset ${
              view === key ? 'bg-blue-700 text-white ring-blue-700' : 'bg-white text-slate-800 ring-slate-300 hover:bg-slate-100'
            }`}
          >
            {VIEWS[key].label}
          </button>
        ))}
      </nav>

      <div className="card flex flex-wrap items-end gap-3">
        <form onSubmit={onSearch} role="search" className="flex min-w-60 flex-1 items-end gap-2">
          <div className="flex flex-1 flex-col gap-1">
            <label htmlFor="ticket-search" className="text-sm font-medium text-slate-700">
              Search number or subject
            </label>
            <input id="ticket-search" className="input" value={search} onChange={(e) => setSearch(e.target.value)} />
          </div>
          <button type="submit" className="btn-secondary">
            Search
          </button>
        </form>
        <FilterSelect
          id="filter-status"
          label="Status"
          value={params.get('status') ?? ''}
          onChange={(v) => update({ status: v })}
          options={(vocab.data?.ticketStatuses ?? []).map((s) => [s, statusLabel(s)])}
        />
        <FilterSelect
          id="filter-priority"
          label="Priority"
          value={params.get('priority') ?? ''}
          onChange={(v) => update({ priority: v })}
          options={(vocab.data?.priorities ?? []).map((p) => [p, p])}
        />
        <FilterSelect
          id="filter-category"
          label="Category"
          value={params.get('category') ?? ''}
          onChange={(v) => update({ category: v })}
          options={(vocab.data?.categories ?? []).map((c) => [c, humanize(c)])}
        />
        <FilterSelect
          id="filter-sort"
          label="Sort by"
          value={params.get('sort') ?? ''}
          onChange={(v) => update({ sort: v })}
          options={SORTS.map(([v, l]) => [v, l])}
          emptyLabel="Default"
        />
      </div>

      {list.isPending ? (
        <Spinner label="Loading tickets…" />
      ) : list.isError ? (
        <ErrorAlert error={list.error} onRetry={() => void list.refetch()} />
      ) : tickets.length === 0 ? (
        <p className="card text-sm text-slate-600">No tickets match this view.</p>
      ) : (
        <div className="overflow-x-auto rounded-lg border border-slate-200 bg-white shadow-sm">
          <table className="min-w-full divide-y divide-slate-200 text-sm">
            <caption className="sr-only">{VIEWS[view].label}</caption>
            <thead className="bg-slate-50 text-left text-xs font-semibold uppercase tracking-wide text-slate-700">
              <tr>
                <th scope="col" className="px-3 py-2">Ticket</th>
                <th scope="col" className="px-3 py-2">Status</th>
                <th scope="col" className="px-3 py-2">Priority</th>
                <th scope="col" className="px-3 py-2">Requester</th>
                <th scope="col" className="px-3 py-2">Assignee</th>
                <th scope="col" className="px-3 py-2">SLA</th>
                <th scope="col" className="px-3 py-2">Updated</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {tickets.map((t) => (
                <tr key={t.id} className="hover:bg-slate-50">
                  <td className="px-3 py-2">
                    <Link to={`/tickets/${t.id}`} className="font-medium text-blue-800 hover:underline">
                      <span className="mr-2 font-mono text-xs text-slate-700">{t.number}</span>
                      {t.subject}
                    </Link>
                    {t.category && <div className="text-xs text-slate-600">{humanize(t.category)}</div>}
                  </td>
                  <td className="px-3 py-2">
                    <StatusBadge status={t.status} />
                  </td>
                  <td className="px-3 py-2">
                    <PriorityBadge priority={t.priority} />
                  </td>
                  <td className="px-3 py-2 text-slate-800">{t.requester.displayName}</td>
                  <td className="px-3 py-2 text-slate-800">
                    {t.assignee?.displayName ?? <span className="text-slate-600">Unassigned</span>}
                    {t.assignmentGroup && <div className="text-xs text-slate-600">{t.assignmentGroup}</div>}
                  </td>
                  <td className="px-3 py-2">
                    <SlaBadge ticket={t} />
                  </td>
                  <td className="px-3 py-2 text-slate-700" title={t.updatedAt}>
                    {relativeTime(t.updatedAt)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {list.hasNextPage && (
        <button type="button" className="btn-secondary self-center" disabled={list.isFetchingNextPage} onClick={() => void list.fetchNextPage()}>
          {list.isFetchingNextPage ? 'Loading…' : 'Load more'}
        </button>
      )}
    </div>
  );
}

function FilterSelect({
  id,
  label,
  value,
  onChange,
  options,
  emptyLabel = 'Any',
}: {
  id: string;
  label: string;
  value: string;
  onChange: (v: string | undefined) => void;
  options: (readonly [string, string])[];
  emptyLabel?: string;
}) {
  return (
    <div className="flex flex-col gap-1">
      <label htmlFor={id} className="text-sm font-medium text-slate-700">
        {label}
      </label>
      <select id={id} className="input" value={value} onChange={(e) => onChange(e.target.value || undefined)}>
        <option value="">{emptyLabel}</option>
        {options.map(([v, l]) => (
          <option key={v} value={v}>
            {l}
          </option>
        ))}
      </select>
    </div>
  );
}
