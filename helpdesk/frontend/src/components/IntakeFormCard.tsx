import { useId, useState } from 'react';
import { ApiError, submitEscalation, type IntakeForm } from '../lib/api';
import { getAuthHeaders } from '../lib/auth';

type Status = { kind: 'editing' } | { kind: 'sending' } | { kind: 'sent' } | { kind: 'error'; message: string };

export function IntakeFormCard({
  form,
  conversationId,
  escalationToken,
}: {
  form: IntakeForm;
  conversationId: string;
  escalationToken: string;
}) {
  const idPrefix = useId();
  const [status, setStatus] = useState<Status>({ kind: 'editing' });
  const [values, setValues] = useState<Record<string, string>>({});
  const locked = status.kind === 'sending' || status.kind === 'sent';

  async function send() {
    setStatus({ kind: 'sending' });
    try {
      await submitEscalation(
        {
          conversationId,
          escalationToken,
          formTitle: form.title,
          answers: form.fields.map((field) => ({ label: field.label, value: values[field.id]?.trim() ?? '' })),
        },
        getAuthHeaders,
      );
      setStatus({ kind: 'sent' });
    } catch (error) {
      const message =
        error instanceof ApiError ? error.message : 'The service desk could not be reached. Please try again.';
      setStatus({ kind: 'error', message });
    }
  }

  return (
    <section className="mt-4 p-4 rounded border border-sky-500/30 bg-sky-950/20 max-w-md">
      <h4 className="text-sm font-semibold text-sky-300 mb-1">{form.title}</h4>
      <p className="text-xs text-zinc-400 mb-3">
        Your request will be sent to the service desk together with these details.
      </p>

      <form
        className="space-y-3"
        onSubmit={(event) => {
          event.preventDefault();
          if (!locked) void send();
        }}
      >
        <fieldset disabled={locked} className="space-y-3">
          {form.fields.map((field) => (
            <div key={field.id}>
              <label htmlFor={`${idPrefix}-${field.id}`} className="block text-[11px] font-medium text-zinc-300 mb-1">
                {field.label}
                {field.required && <span aria-hidden="true"> *</span>}
              </label>
              {field.inputType === 'textarea' ? (
                <textarea
                  id={`${idPrefix}-${field.id}`}
                  name={field.id}
                  required={field.required}
                  value={values[field.id] ?? ''}
                  onChange={(event) => setValues({ ...values, [field.id]: event.target.value })}
                  aria-describedby={`${idPrefix}-${field.id}-description`}
                  className="w-full min-h-20 text-xs p-2 rounded bg-zinc-950 border border-zinc-700 text-zinc-200 disabled:opacity-60"
                />
              ) : (
                <input
                  id={`${idPrefix}-${field.id}`}
                  name={field.id}
                  type="text"
                  required={field.required}
                  value={values[field.id] ?? ''}
                  onChange={(event) => setValues({ ...values, [field.id]: event.target.value })}
                  aria-describedby={`${idPrefix}-${field.id}-description`}
                  className="w-full text-xs p-2 rounded bg-zinc-950 border border-zinc-700 text-zinc-200 disabled:opacity-60"
                />
              )}
              <p id={`${idPrefix}-${field.id}-description`} className="mt-1 text-[11px] text-zinc-500">
                {field.description}
              </p>
            </div>
          ))}
          {status.kind !== 'sent' && (
            <button
              type="submit"
              className="w-full py-2 bg-sky-700 hover:bg-sky-600 transition-colors rounded text-xs text-white font-semibold disabled:opacity-60"
            >
              {status.kind === 'sending' ? 'Sending…' : form.submitLabel}
            </button>
          )}
        </fieldset>
      </form>

      {status.kind === 'sent' && (
        <p role="status" className="mt-3 text-xs text-emerald-400">
          Sent to the service desk. You'll receive a ticket reference by email.
        </p>
      )}
      {status.kind === 'error' && (
        <p role="alert" className="mt-3 text-xs text-red-400">
          {status.message}
        </p>
      )}
    </section>
  );
}
