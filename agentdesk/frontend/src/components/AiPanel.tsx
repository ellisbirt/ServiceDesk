import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useNavigate } from 'react-router';
import { api } from '../lib/api';
import { humanize } from '../lib/format';
import { keys, useAssistInfo } from '../lib/queries';
import type { ImproveMode, KbArticleInput, ReplyWithKbResult, Suggestion, Ticket } from '../lib/types';
import { citationText, type ComposerState } from './Composer';
import { AiLabel, Badge, ErrorAlert, Field, MarkdownView, Spinner } from './ui';

const DEFAULT_MODES: ImproveMode[] = ['polish', 'shorten', 'friendlier', 'formal', 'expand'];

export function insertCitation(replyBody: string, placeholder: string, citation: string): string {
  if (placeholder && replyBody.includes(placeholder)) return replyBody.split(placeholder).join(citation);
  return `${replyBody.trimEnd()}\n\nSee ${citation}.`;
}

export function suggestionToText(s: Suggestion): string {
  return [`**${s.title}**`, '', ...s.steps.map((step, i) => `${i + 1}. ${step}`)].join('\n');
}

export function AiPanel({
  ticket,
  composer,
  onComposerChange,
}: {
  ticket: Ticket;
  composer: ComposerState;
  onComposerChange: (next: ComposerState) => void;
}) {
  const assist = useAssistInfo();
  const [instructions, setInstructions] = useState('');

  if (assist.isPending) return <Spinner label="Checking AI assistance…" />;
  if (assist.isError || !assist.data?.available)
    return (
      <section aria-labelledby="ai-h" className="card text-sm text-slate-700">
        <h2 id="ai-h" className="mb-1 font-semibold text-slate-900">
          AI assistant
        </h2>
        AI assistance is not available right now. You can still reply and write articles by hand.
      </section>
    );

  const info = assist.data;
  const instr = instructions.trim() || undefined;

  return (
    <section aria-labelledby="ai-h" className="card flex flex-col gap-3 border-violet-200">
      <div className="flex flex-wrap items-center gap-2">
        <h2 id="ai-h" className="font-semibold text-slate-900">
          AI assistant
        </h2>
        <Badge tone="violet" title={`${info.provider ?? ''} ${info.model ?? ''}`.trim()}>
          {info.offline ? 'Offline model' : info.model ?? info.provider ?? 'LLM'}
        </Badge>
      </div>
      <p className="text-xs text-slate-600">AI output is a draft. Check it before anything reaches the requester.</p>
      <Field label="Extra instructions (optional)" htmlFor="ai-instructions" hint="For example: “the user is on a Mac” or “keep it brief”.">
        <input id="ai-instructions" className="input" value={instructions} onChange={(e) => setInstructions(e.target.value)} />
      </Field>
      <Suggestions ticket={ticket} instructions={instr} composer={composer} onComposerChange={onComposerChange} />
      <DraftReply ticket={ticket} instructions={instr} composer={composer} onComposerChange={onComposerChange} />
      <Improve ticket={ticket} instructions={instr} composer={composer} onComposerChange={onComposerChange} modes={info.improveModes ?? DEFAULT_MODES} />
      <ReplyWithKb ticket={ticket} instructions={instr} composer={composer} onComposerChange={onComposerChange} />
      <DraftKb ticket={ticket} instructions={instr} />
    </section>
  );
}

interface ToolProps {
  ticket: Ticket;
  instructions?: string;
  composer: ComposerState;
  onComposerChange: (next: ComposerState) => void;
}

function Suggestions({ ticket, instructions, composer, onComposerChange }: ToolProps) {
  const suggest = useMutation({ mutationFn: () => api.suggestSolutions(ticket.id, instructions) });
  const data = suggest.data;
  return (
    <div className="flex flex-col gap-2 border-t border-slate-200 pt-3">
      <button type="button" className="btn-ai" disabled={suggest.isPending} onClick={() => suggest.mutate()}>
        {suggest.isPending ? 'Thinking…' : 'Suggest solutions'}
      </button>
      <ErrorAlert error={suggest.error} onRetry={() => suggest.mutate()} />
      {data && (
        <div className="flex flex-col gap-2 text-sm" aria-live="polite">
          <div className="flex items-center gap-2">
            <h3 className="font-semibold text-slate-900">Suggested solutions</h3>
            <AiLabel ai={data.aiAssisted} />
          </div>
          {data.summary && <p className="text-slate-800">{data.summary}</p>}
          <ol className="flex flex-col gap-2">
            {data.suggestions.map((s, i) => (
              <li key={i} className="rounded-md border border-violet-200 bg-violet-50 p-2">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-medium text-slate-900">{s.title}</span>
                  <Badge tone="slate">{humanize(s.confidence)} confidence</Badge>
                </div>
                <ol className="ml-5 mt-1 list-decimal text-slate-800">
                  {s.steps.map((step, j) => (
                    <li key={j}>{step}</li>
                  ))}
                </ol>
                {s.rationale && <p className="mt-1 text-xs text-slate-700">{s.rationale}</p>}
                <button
                  type="button"
                  className="btn-secondary mt-2 text-xs"
                  onClick={() => {
                    const sep = composer.body.trim() ? '\n\n' : '';
                    onComposerChange({ ...composer, body: `${composer.body.trimEnd()}${sep}${suggestionToText(s)}`, aiAssisted: data.aiAssisted });
                  }}
                >
                  Add to reply
                </button>
              </li>
            ))}
          </ol>
          {data.clarifyingQuestions.length > 0 && (
            <div>
              <h4 className="font-medium text-slate-900">Questions to ask the requester</h4>
              <ul className="ml-5 list-disc text-slate-800">
                {data.clarifyingQuestions.map((q, i) => (
                  <li key={i}>{q}</li>
                ))}
              </ul>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function DraftReply({ ticket, instructions, composer, onComposerChange }: ToolProps) {
  const draft = useMutation({
    mutationFn: () =>
      api.draftReply(ticket.id, {
        instructions,
        visibility: composer.visibility,
        kbArticleIds: composer.cited.length ? composer.cited.map((c) => c.id) : undefined,
      }),
  });
  return (
    <div className="flex flex-col gap-2 border-t border-slate-200 pt-3">
      <button type="button" className="btn-ai" disabled={draft.isPending} onClick={() => draft.mutate()}>
        {draft.isPending ? 'Drafting…' : composer.visibility === 'internal' ? 'Draft internal note' : 'Draft reply'}
      </button>
      <ErrorAlert error={draft.error} onRetry={() => draft.mutate()} />
      {draft.data && (
        <Preview
          title="Draft"
          text={draft.data.body}
          ai={draft.data.aiAssisted}
          actionLabel={composer.body.trim() ? 'Replace reply text' : 'Use in reply'}
          onAccept={() => {
            onComposerChange({ ...composer, body: draft.data.body, aiAssisted: draft.data.aiAssisted });
            draft.reset();
          }}
          onDiscard={draft.reset}
        />
      )}
    </div>
  );
}

function Improve({ ticket, instructions, composer, onComposerChange, modes }: ToolProps & { modes: ImproveMode[] }) {
  const [mode, setMode] = useState<ImproveMode>(modes[0] ?? 'polish');
  const improve = useMutation({ mutationFn: () => api.improve(ticket.id, { text: composer.body, mode, instructions }) });
  const empty = !composer.body.trim();
  return (
    <div className="flex flex-col gap-2 border-t border-slate-200 pt-3">
      <div className="flex items-end gap-2">
        <div className="flex flex-1 flex-col gap-1">
          <label htmlFor="ai-improve-mode" className="text-sm font-medium text-slate-700">
            Improve my reply
          </label>
          <select id="ai-improve-mode" className="input" value={mode} onChange={(e) => setMode(e.target.value as ImproveMode)}>
            {modes.map((m) => (
              <option key={m} value={m}>
                {humanize(m)}
              </option>
            ))}
          </select>
        </div>
        <button type="button" className="btn-ai" disabled={empty || improve.isPending} onClick={() => improve.mutate()}>
          {improve.isPending ? 'Rewriting…' : 'Improve'}
        </button>
      </div>
      {empty && <p className="text-xs text-slate-600">Write something in the reply box first.</p>}
      <ErrorAlert error={improve.error} onRetry={() => improve.mutate()} />
      {improve.data && (
        <Preview
          title={`${humanize(mode)} version`}
          text={improve.data.text}
          ai={improve.data.aiAssisted}
          actionLabel="Replace reply text"
          onAccept={() => {
            onComposerChange({ ...composer, body: improve.data.text, aiAssisted: improve.data.aiAssisted });
            improve.reset();
          }}
          onDiscard={improve.reset}
        />
      )}
    </div>
  );
}

function DraftKb({ ticket, instructions }: { ticket: Ticket; instructions?: string }) {
  const navigate = useNavigate();
  const draft = useMutation({
    mutationFn: () => api.draftKb(ticket.id, instructions),
    onSuccess: (result) =>
      navigate('/kb/new', {
        state: {
          draft: { ...result.article, aiAssisted: result.aiAssisted, sourceTicketIds: result.article.sourceTicketIds ?? [ticket.id] },
          sourceTicketId: ticket.id,
        },
      }),
  });
  return (
    <div className="flex flex-col gap-2 border-t border-slate-200 pt-3">
      <button type="button" className="btn-ai" disabled={draft.isPending} onClick={() => draft.mutate()}>
        {draft.isPending ? 'Drafting article…' : 'Draft KB article only'}
      </button>
      <p className="text-xs text-slate-600">Opens the article editor with a draft built from this ticket.</p>
      <ErrorAlert error={draft.error} onRetry={() => draft.mutate()} />
    </div>
  );
}

function ReplyWithKb({ ticket, instructions, composer, onComposerChange }: ToolProps) {
  const generate = useMutation({ mutationFn: () => api.replyWithKb(ticket.id, instructions) });
  return (
    <div className="flex flex-col gap-2 border-t border-slate-200 pt-3">
      <div className="flex items-center gap-2">
        <button type="button" className="btn-ai flex-1" disabled={generate.isPending} onClick={() => generate.mutate()}>
          {generate.isPending ? 'Drafting article and reply…' : 'Reply with new KB article'}
        </button>
        <Badge tone="green">Recommended</Badge>
      </div>
      <p className="text-xs text-slate-600">
        Writes a reusable knowledge base article and a reply that cites it, so the next person with this problem can find the
        answer.
      </p>
      <ErrorAlert error={generate.error} onRetry={() => generate.mutate()} />
      {generate.data && (
        <ReplyWithKbReview
          key={generate.submittedAt}
          ticket={ticket}
          result={generate.data}
          composer={composer}
          onComposerChange={onComposerChange}
          onDone={generate.reset}
        />
      )}
    </div>
  );
}

export function ReplyWithKbReview({
  ticket,
  result,
  composer,
  onComposerChange,
  onDone,
}: {
  ticket: Ticket;
  result: ReplyWithKbResult;
  composer: ComposerState;
  onComposerChange: (next: ComposerState) => void;
  onDone: () => void;
}) {
  const queryClient = useQueryClient();
  const [article, setArticle] = useState<KbArticleInput>({
    ...result.article,
    aiAssisted: result.aiAssisted,
    sourceTicketIds: result.article.sourceTicketIds ?? [ticket.id],
  });
  const [reply, setReply] = useState(result.replyBody);
  const [published, setPublished] = useState<string | undefined>();
  const setField = (field: keyof KbArticleInput) => (e: { target: { value: string } }) => setArticle({ ...article, [field]: e.target.value });

  const save = useMutation({
    mutationFn: async (sendNow: boolean) => {
      const created = await api.createKb(article);
      const cited = { id: created.id, number: created.number, title: created.title };
      const body = insertCitation(reply, result.placeholder, citationText(cited));
      if (!sendNow) return { created, cited, body, sent: undefined };
      const sent = await api.addComment(ticket.id, {
        visibility: 'public',
        body,
        citedKbArticleIds: [created.id],
        aiAssisted: result.aiAssisted,
      });
      return { created, cited, body, sent };
    },
    onSuccess: ({ created, cited, body, sent }) => {
      queryClient.setQueryData(keys.kbArticle(created.id), created);
      void queryClient.invalidateQueries({ queryKey: ['kb'] });
      if (sent) {
        queryClient.setQueryData(keys.ticket(ticket.id), sent.ticket);
        void queryClient.invalidateQueries({ queryKey: keys.timeline(ticket.id) });
        void queryClient.invalidateQueries({ queryKey: keys.ticketLists });
        setPublished(`${created.number} saved and published, reply sent.`);
        return;
      }
      onComposerChange({
        ...composer,
        visibility: 'public',
        body,
        aiAssisted: result.aiAssisted,
        cited: [...composer.cited.filter((c) => c.id !== cited.id), cited],
      });
      onDone();
    },
  });

  if (published)
    return (
      <div role="status" className="rounded-md border border-green-300 bg-green-50 p-2 text-sm text-green-900">
        {published}{' '}
        <button type="button" className="underline" onClick={onDone}>
          Dismiss
        </button>
      </div>
    );

  return (
    <div role="group" className="flex flex-col gap-3 rounded-md border border-violet-200 bg-violet-50 p-3" aria-label="Review article and reply">
      <div className="flex items-center gap-2">
        <h3 className="font-semibold text-slate-900">Review before saving</h3>
        <AiLabel ai={result.aiAssisted} />
      </div>
      <fieldset className="flex flex-col gap-2">
        <legend className="mb-1 text-sm font-semibold text-slate-800">New article</legend>
        <Field label="Title" htmlFor="rk-title">
          <input id="rk-title" className="input" value={article.title} onChange={setField('title')} />
        </Field>
        <Field label="Summary" htmlFor="rk-summary">
          <textarea id="rk-summary" className="input" value={article.summary} onChange={setField('summary')} />
        </Field>
        <Field label="Symptoms" htmlFor="rk-symptoms">
          <textarea id="rk-symptoms" className="input" value={article.symptoms ?? ''} onChange={setField('symptoms')} />
        </Field>
        <Field label="Cause" htmlFor="rk-cause">
          <textarea id="rk-cause" className="input" value={article.cause ?? ''} onChange={setField('cause')} />
        </Field>
        <Field label="Resolution" htmlFor="rk-resolution">
          <textarea id="rk-resolution" className="input" value={article.resolution ?? ''} onChange={setField('resolution')} />
        </Field>
        <Field label="Article body" htmlFor="rk-body" hint="Markdown.">
          <textarea id="rk-body" className="input min-h-40 font-mono text-xs" value={article.body} onChange={setField('body')} />
        </Field>
      </fieldset>
      <Field
        label="Reply to requester"
        htmlFor="rk-reply"
        hint={`${result.placeholder} is replaced with the article number and title when you save.`}
      >
        <textarea id="rk-reply" className="input min-h-32" value={reply} onChange={(e) => setReply(e.target.value)} />
      </Field>
      <ErrorAlert error={save.error} />
      <div className="flex flex-wrap justify-end gap-2">
        <button type="button" className="btn-secondary" onClick={onDone} disabled={save.isPending}>
          Discard
        </button>
        <button type="button" className="btn-secondary" disabled={save.isPending || !article.title.trim()} onClick={() => save.mutate(false)}>
          Save article &amp; edit reply
        </button>
        <button
          type="button"
          className="btn-primary"
          disabled={save.isPending || !article.title.trim() || !reply.trim()}
          onClick={() => save.mutate(true)}
        >
          {save.isPending ? 'Saving…' : 'Save article & send reply'}
        </button>
      </div>
    </div>
  );
}

function Preview({
  title,
  text,
  ai,
  actionLabel,
  onAccept,
  onDiscard,
}: {
  title: string;
  text: string;
  ai: ReplyWithKbResult['aiAssisted'];
  actionLabel: string;
  onAccept: () => void;
  onDiscard: () => void;
}) {
  return (
    <div className="rounded-md border border-violet-200 bg-violet-50 p-2 text-sm" aria-live="polite">
      <div className="mb-1 flex items-center gap-2">
        <h3 className="font-semibold text-slate-900">{title}</h3>
        <AiLabel ai={ai} />
      </div>
      <MarkdownView>{text}</MarkdownView>
      <div className="mt-2 flex justify-end gap-2">
        <button type="button" className="btn-secondary text-xs" onClick={onDiscard}>
          Discard
        </button>
        <button type="button" className="btn-primary text-xs" onClick={onAccept}>
          {actionLabel}
        </button>
      </div>
    </div>
  );
}
