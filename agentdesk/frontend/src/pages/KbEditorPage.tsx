import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { Link, useLocation, useNavigate, useParams } from 'react-router';
import { AiLabel, EmbeddingBadge, ErrorAlert, Field, KbStatusBadge, MarkdownView, Spinner } from '../components/ui';
import { api, ApiError } from '../lib/api';
import { formatDateTime, humanize } from '../lib/format';
import { keys, useKbArticle, useVocabulary } from '../lib/queries';
import type { KbArticle, KbArticleInput, Visibility } from '../lib/types';

export interface KbEditorLocationState {
  draft?: KbArticleInput;
  sourceTicketId?: string;
}

interface FormState {
  title: string;
  summary: string;
  body: string;
  symptoms: string;
  cause: string;
  resolution: string;
  category: string;
  tags: string;
  visibility: Visibility;
}

const toForm = (a?: Partial<KbArticleInput>): FormState => ({
  title: a?.title ?? '',
  summary: a?.summary ?? '',
  body: a?.body ?? '',
  symptoms: a?.symptoms ?? '',
  cause: a?.cause ?? '',
  resolution: a?.resolution ?? '',
  category: a?.category ?? '',
  tags: (a?.tags ?? []).join(', '),
  visibility: a?.visibility ?? 'public',
});

const opt = (s: string) => (s.trim() ? s.trim() : undefined);

export function parseTags(s: string): string[] {
  return [...new Set(s.split(',').map((t) => t.trim()).filter(Boolean))];
}

const toInput = (f: FormState, extra?: Pick<KbArticleInput, 'sourceTicketIds' | 'aiAssisted'>): KbArticleInput => ({
  title: f.title.trim(),
  summary: f.summary.trim(),
  body: f.body.trim(),
  symptoms: opt(f.symptoms),
  cause: opt(f.cause),
  resolution: opt(f.resolution),
  category: opt(f.category),
  tags: parseTags(f.tags),
  visibility: f.visibility,
  ...extra,
});

const sameForm = (a: FormState, b: FormState) => (Object.keys(a) as (keyof FormState)[]).every((k) => a[k] === b[k]);

export function KbEditorPage() {
  const { id } = useParams();
  const location = useLocation();
  const article = useKbArticle(id);

  if (!id) {
    const state = (location.state ?? {}) as KbEditorLocationState;
    return <KbEditor key="new" draft={state.draft} sourceTicketId={state.sourceTicketId} />;
  }
  if (article.isPending) return <Spinner label="Loading article…" />;
  if (article.isError) {
    if (article.error instanceof ApiError && article.error.status === 404)
      return (
        <div className="card">
          <h1 className="text-lg font-semibold">Article not found</h1>
          <p className="mt-1 text-sm text-slate-700">
            <Link to="/kb" className="text-blue-700 underline">
              Back to the knowledge base
            </Link>
          </p>
        </div>
      );
    return <ErrorAlert error={article.error} onRetry={() => void article.refetch()} />;
  }
  return <KbEditor key={id} article={article.data} />;
}

function KbEditor({ article, draft, sourceTicketId }: { article?: KbArticle; draft?: KbArticleInput; sourceTicketId?: string }) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const vocab = useVocabulary();
  const [base, setBase] = useState<KbArticle | undefined>(article);
  const [form, setForm] = useState<FormState>(() => toForm(article ?? draft));
  const [preview, setPreview] = useState(false);

  const isNew = !article;
  const baseForm = toForm(base ?? draft);
  const dirty = isNew ? true : !sameForm(form, baseForm);

  // Adopt changes made elsewhere (live updates, other tabs) when nothing local would be lost.
  if (article && base && article.eTag !== base.eTag && !dirty) {
    setBase(article);
    setForm(toForm(article));
  }
  const current = base ?? article;
  const changedElsewhere = Boolean(article && current && article.eTag !== current.eTag);
  const archived = current?.status === 'archived';

  const adopt = (saved: KbArticle) => {
    queryClient.setQueryData(keys.kbArticle(saved.id), saved);
    void queryClient.invalidateQueries({ queryKey: keys.kbLists });
    void queryClient.invalidateQueries({ queryKey: keys.kbVersions(saved.id) });
    setBase(saved);
    setForm(toForm(saved));
  };

  const save = useMutation({
    mutationFn: () =>
      isNew
        ? api.createKb(
            toInput(form, {
              aiAssisted: draft?.aiAssisted,
              sourceTicketIds: draft?.sourceTicketIds ?? (sourceTicketId ? [sourceTicketId] : undefined),
            }),
          )
        : api.updateKb(current!.id, toInput(form), current!.eTag),
    onSuccess: (saved) => {
      if (isNew) {
        queryClient.setQueryData(keys.kbArticle(saved.id), saved);
        void queryClient.invalidateQueries({ queryKey: keys.kbLists });
        navigate(`/kb/${saved.id}`, { replace: true });
        return;
      }
      adopt(saved);
    },
  });

  const action = useMutation({
    mutationFn: (a: 'publish' | 'archive' | 'restore' | 'reembed') => api.kbAction(current!.id, a, current!.eTag),
    onSuccess: adopt,
  });

  const reload = () => {
    if (article) {
      setBase(article);
      setForm(toForm(article));
    }
    save.reset();
    action.reset();
    if (current) void queryClient.invalidateQueries({ queryKey: keys.kbArticle(current.id) });
  };

  const onSubmit = (e: FormEvent) => {
    e.preventDefault();
    save.mutate();
  };

  const set = (field: keyof FormState) => (e: { target: { value: string } }) => setForm({ ...form, [field]: e.target.value });
  const busy = save.isPending || action.isPending;
  const conflict = [save.error, action.error].some((e) => e instanceof ApiError && e.isConflict);
  const sources = current?.sourceTicketIds ?? draft?.sourceTicketIds ?? (sourceTicketId ? [sourceTicketId] : []);
  const ai = current?.aiAssisted ?? draft?.aiAssisted;

  return (
    <div className="flex flex-col gap-4">
      <nav aria-label="Breadcrumb" className="text-sm">
        <Link to="/kb" className="text-blue-700 underline">
          Knowledge base
        </Link>{' '}
        / <span aria-current="page">{current?.number ?? 'New article'}</span>
      </nav>
      <div className="flex flex-wrap items-center gap-2">
        <h1 className="text-2xl font-semibold">{current ? `${current.number} — ${current.title}` : 'New article'}</h1>
        {current && <KbStatusBadge status={current.status} />}
        {current && <span className="text-sm text-slate-700">v{current.version}</span>}
        {current && <EmbeddingBadge embedding={current.embedding} />}
        <AiLabel ai={ai} />
      </div>
      {isNew && draft && (
        <p className="rounded-md border border-violet-200 bg-violet-50 p-2 text-sm text-violet-900">
          This draft was written by AI from a ticket. Check every step before saving.
        </p>
      )}
      {changedElsewhere && (
        <div role="status" className="rounded-md border border-amber-300 bg-amber-50 p-2 text-sm text-amber-900">
          Someone else changed this article while you were editing.{' '}
          <button type="button" className="underline" onClick={reload}>
            Load their version (discards your edits)
          </button>
        </div>
      )}
      {archived && (
        <p className="rounded-md border border-slate-300 bg-slate-50 p-2 text-sm text-slate-800">
          This article is archived and can't be edited or cited. Restore it to a draft to work on it again.
        </p>
      )}

      <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_20rem]">
        <form onSubmit={onSubmit} className="card flex flex-col gap-3" aria-label="Article">
          <fieldset disabled={archived || busy} className="flex flex-col gap-3">
            <Field label="Title" htmlFor="kb-title">
              <input id="kb-title" className="input" required maxLength={200} value={form.title} onChange={set('title')} />
            </Field>
            <Field label="Summary" htmlFor="kb-summary" hint="One or two sentences shown in lists and citations.">
              <textarea id="kb-summary" className="input" required value={form.summary} onChange={set('summary')} />
            </Field>
            <div className="grid gap-3 md:grid-cols-3">
              <Field label="Symptoms" htmlFor="kb-symptoms">
                <textarea id="kb-symptoms" className="input min-h-24" value={form.symptoms} onChange={set('symptoms')} />
              </Field>
              <Field label="Cause" htmlFor="kb-cause">
                <textarea id="kb-cause" className="input min-h-24" value={form.cause} onChange={set('cause')} />
              </Field>
              <Field label="Resolution" htmlFor="kb-resolution">
                <textarea id="kb-resolution" className="input min-h-24" value={form.resolution} onChange={set('resolution')} />
              </Field>
            </div>
            <div className="flex flex-col gap-1">
              <div className="flex items-center justify-between">
                <label htmlFor="kb-body" className="text-sm font-medium text-slate-700">
                  Article body (Markdown)
                </label>
                <button type="button" className="text-sm text-blue-700 underline" aria-pressed={preview} onClick={() => setPreview(!preview)}>
                  {preview ? 'Edit' : 'Preview'}
                </button>
              </div>
              {preview ? (
                <div role="group" className="min-h-60 rounded-md border border-slate-300 p-3" aria-label="Body preview">
                  <MarkdownView>{form.body || '_Nothing to preview._'}</MarkdownView>
                </div>
              ) : (
                <textarea id="kb-body" className="input min-h-60 font-mono text-sm" required value={form.body} onChange={set('body')} />
              )}
            </div>
            <div className="grid gap-3 md:grid-cols-3">
              <Field label="Category" htmlFor="kb-category-input">
                <select id="kb-category-input" className="input" value={form.category} onChange={set('category')}>
                  <option value="">None</option>
                  {(vocab.data?.categories ?? []).map((c) => (
                    <option key={c} value={c}>
                      {humanize(c)}
                    </option>
                  ))}
                </select>
              </Field>
              <Field label="Tags" htmlFor="kb-tags" hint="Separate with commas.">
                <input id="kb-tags" className="input" value={form.tags} onChange={set('tags')} />
              </Field>
              <Field label="Visibility" htmlFor="kb-visibility">
                <select id="kb-visibility" className="input" value={form.visibility} onChange={set('visibility')}>
                  <option value="public">Public (can be sent to requesters)</option>
                  <option value="internal">Internal (agents only)</option>
                </select>
              </Field>
            </div>
          </fieldset>
          <ErrorAlert error={save.error} onRetry={conflict ? reload : undefined} />
          {!archived && (
            <div className="flex justify-end gap-2">
              {!isNew && (
                <button type="button" className="btn-secondary" disabled={!dirty || busy} onClick={reload}>
                  Discard changes
                </button>
              )}
              <button type="submit" className="btn-primary" disabled={busy || (!isNew && !dirty)}>
                {save.isPending ? 'Saving…' : isNew ? 'Save draft' : 'Save changes'}
              </button>
            </div>
          )}
        </form>

        <aside className="flex flex-col gap-4" aria-label="Article details">
          {current && (
            <section aria-labelledby="kb-lifecycle-h" className="card flex flex-col gap-2 text-sm">
              <h2 id="kb-lifecycle-h" className="font-semibold">
                Lifecycle
              </h2>
              {current.status === 'draft' && (
                <button type="button" className="btn-primary" disabled={busy || dirty} onClick={() => action.mutate('publish')}>
                  Publish
                </button>
              )}
              {current.status !== 'archived' && (
                <button type="button" className="btn-secondary" disabled={busy || dirty} onClick={() => action.mutate('archive')}>
                  Archive
                </button>
              )}
              {current.status === 'archived' && (
                <button type="button" className="btn-secondary" disabled={busy} onClick={() => action.mutate('restore')}>
                  Restore to draft
                </button>
              )}
              {dirty && current.status !== 'archived' && <p className="text-xs text-slate-600">Save or discard your changes first.</p>}
              <ErrorAlert error={action.error} onRetry={conflict ? reload : undefined} />
              <dl className="mt-1 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1">
                <dt className="text-slate-600">Created</dt>
                <dd>{formatDateTime(current.createdAt)}</dd>
                <dt className="text-slate-600">Updated</dt>
                <dd>
                  {formatDateTime(current.updatedAt)}
                  {current.updatedBy && ` by ${current.updatedBy.displayName}`}
                </dd>
                {current.publishedAt && (
                  <>
                    <dt className="text-slate-600">Published</dt>
                    <dd>{formatDateTime(current.publishedAt)}</dd>
                  </>
                )}
                {current.reviewDueAt && (
                  <>
                    <dt className="text-slate-600">Review due</dt>
                    <dd>{formatDateTime(current.reviewDueAt)}</dd>
                  </>
                )}
              </dl>
            </section>
          )}
          {current && (
            <section aria-labelledby="kb-embed-h" className="card flex flex-col gap-2 text-sm">
              <h2 id="kb-embed-h" className="font-semibold">
                Embedding
              </h2>
              <div>
                <EmbeddingBadge embedding={current.embedding} />
              </div>
              {current.embedding?.model && (
                <p className="text-slate-700">
                  {current.embedding.provider} {current.embedding.model}
                  {current.embedding.dimensions ? ` · ${current.embedding.dimensions} dimensions` : ''}
                  {current.embedding.embeddedAt ? ` · ${formatDateTime(current.embedding.embeddedAt)}` : ''}
                </p>
              )}
              {current.embedding?.error && <p className="text-red-800">{current.embedding.error}</p>}
              <p className="text-xs text-slate-600">Stored with the article for future search. Generated each time the content is saved.</p>
              <button type="button" className="btn-secondary" disabled={busy || dirty} onClick={() => action.mutate('reembed')}>
                Re-generate embedding
              </button>
            </section>
          )}
          {sources.length > 0 && (
            <section aria-labelledby="kb-src-h" className="card text-sm">
              <h2 id="kb-src-h" className="mb-1 font-semibold">
                Source tickets
              </h2>
              <ul className="flex flex-col gap-1">
                {sources.map((t) => (
                  <li key={t}>
                    <Link to={`/tickets/${t}`} className="text-blue-700 underline">
                      {t}
                    </Link>
                  </li>
                ))}
              </ul>
            </section>
          )}
          {current && <VersionHistory id={current.id} version={current.version} />}
        </aside>
      </div>
    </div>
  );
}

function VersionHistory({ id, version }: { id: string; version: number }) {
  const [open, setOpen] = useState(false);
  const versions = useQuery({ queryKey: [...keys.kbVersions(id), version], queryFn: () => api.kbVersions(id), enabled: open });
  return (
    <details className="card text-sm" onToggle={(e) => setOpen(e.currentTarget.open)}>
      <summary className="cursor-pointer font-semibold">Version history</summary>
      <div className="mt-2">
        {!open ? null : versions.isPending ? (
          <Spinner label="Loading versions…" />
        ) : versions.isError ? (
          <ErrorAlert error={versions.error} onRetry={() => void versions.refetch()} />
        ) : (
          <ol className="flex flex-col gap-2">
            {[...versions.data].sort((a, b) => b.version - a.version).map((v) => (
              <li key={v.version} className="border-b border-slate-100 pb-1">
                <div className="flex items-center gap-2">
                  <span className="font-medium">v{v.version}</span>
                  <KbStatusBadge status={v.status} />
                </div>
                <div className="text-slate-800">{v.title}</div>
                {v.createdAt && (
                  <div className="text-xs text-slate-600">
                    {formatDateTime(v.createdAt)}
                    {v.createdBy && ` · ${v.createdBy.displayName}`}
                  </div>
                )}
              </li>
            ))}
          </ol>
        )}
      </div>
    </details>
  );
}
