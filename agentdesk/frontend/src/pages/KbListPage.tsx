import { useInfiniteQuery } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { Link, useSearchParams } from 'react-router';
import { EmbeddingBadge, ErrorAlert, KbStatusBadge, Spinner } from '../components/ui';
import { api } from '../lib/api';
import { humanize, relativeTime } from '../lib/format';
import { keys, useVocabulary } from '../lib/queries';
import type { KbQuery } from '../lib/types';

export function KbListPage() {
  const vocab = useVocabulary();
  const [params, setParams] = useSearchParams();
  const status = params.get('status') ?? '';
  const category = params.get('category') ?? '';
  const q = params.get('q') ?? '';
  const [search, setSearch] = useState(q);

  const update = (key: string, value: string) => {
    const next = new URLSearchParams(params);
    if (value) next.set(key, value);
    else next.delete(key);
    setParams(next, { replace: true });
  };

  const query: KbQuery = { status: status || undefined, category: category || undefined, q: q || undefined, pageSize: 25 };
  const list = useInfiniteQuery({
    queryKey: keys.kbList(query),
    queryFn: ({ pageParam, signal }) => api.listKb({ ...query, continuationToken: pageParam }, signal),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.continuationToken ?? undefined,
  });
  const items = list.data?.pages.flatMap((p) => p.items) ?? [];

  const onSearch = (e: FormEvent) => {
    e.preventDefault();
    update('q', search.trim());
  };

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h1 className="text-2xl font-semibold">Knowledge base</h1>
        <Link to="/kb/new" className="btn-primary">
          New article
        </Link>
      </div>

      <div className="card flex flex-wrap items-end gap-3">
        <form role="search" onSubmit={onSearch} className="flex items-end gap-2">
          <div className="flex flex-col gap-1">
            <label htmlFor="kb-search" className="text-sm font-medium text-slate-700">
              Search number, title or tag
            </label>
            <input id="kb-search" type="search" className="input" value={search} onChange={(e) => setSearch(e.target.value)} />
          </div>
          <button type="submit" className="btn-secondary">
            Search
          </button>
        </form>
        <div className="flex flex-col gap-1">
          <label htmlFor="kb-status" className="text-sm font-medium text-slate-700">
            Status
          </label>
          <select id="kb-status" className="input" value={status} onChange={(e) => update('status', e.target.value)}>
            <option value="">Any</option>
            {(vocab.data?.kbStatuses ?? ['draft', 'published', 'archived']).map((s) => (
              <option key={s} value={s}>
                {humanize(s)}
              </option>
            ))}
          </select>
        </div>
        <div className="flex flex-col gap-1">
          <label htmlFor="kb-category" className="text-sm font-medium text-slate-700">
            Category
          </label>
          <select id="kb-category" className="input" value={category} onChange={(e) => update('category', e.target.value)}>
            <option value="">Any</option>
            {(vocab.data?.categories ?? []).map((c) => (
              <option key={c} value={c}>
                {humanize(c)}
              </option>
            ))}
          </select>
        </div>
      </div>

      {list.isPending ? (
        <Spinner label="Loading articles…" />
      ) : list.isError ? (
        <ErrorAlert error={list.error} onRetry={() => void list.refetch()} />
      ) : items.length === 0 ? (
        <p className="card text-sm text-slate-700">No articles match. Try a different filter, or write a new article.</p>
      ) : (
        <div className="card overflow-x-auto p-0">
          <table className="w-full text-left text-sm">
            <caption className="sr-only">Knowledge base articles</caption>
            <thead className="border-b border-slate-200 bg-slate-50 text-slate-700">
              <tr>
                <th scope="col" className="px-3 py-2">
                  Article
                </th>
                <th scope="col" className="px-3 py-2">
                  Status
                </th>
                <th scope="col" className="px-3 py-2">
                  Category
                </th>
                <th scope="col" className="px-3 py-2">
                  Version
                </th>
                <th scope="col" className="px-3 py-2">
                  Embedding
                </th>
                <th scope="col" className="px-3 py-2">
                  Updated
                </th>
              </tr>
            </thead>
            <tbody>
              {items.map((a) => (
                <tr key={a.id} className="border-b border-slate-100 align-top">
                  <td className="px-3 py-2">
                    <Link to={`/kb/${a.id}`} className="font-medium text-blue-700 underline">
                      <span className="font-mono">{a.number}</span> {a.title}
                    </Link>
                    {a.summary && <p className="mt-0.5 line-clamp-2 text-slate-700">{a.summary}</p>}
                  </td>
                  <td className="px-3 py-2">
                    <KbStatusBadge status={a.status} />
                  </td>
                  <td className="px-3 py-2">{a.category ? humanize(a.category) : '—'}</td>
                  <td className="px-3 py-2">v{a.version}</td>
                  <td className="px-3 py-2">
                    <EmbeddingBadge embedding={a.embedding} />
                  </td>
                  <td className="px-3 py-2 whitespace-nowrap">{relativeTime(a.updatedAt)}</td>
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
