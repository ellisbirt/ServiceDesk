import { useId, useState } from 'react';
import type { IntakeForm } from '../lib/api';

export function IntakeFormCard({ form }: { form: IntakeForm }) {
  const idPrefix = useId();
  const [submitted, setSubmitted] = useState(false);
  const [values, setValues] = useState<Record<string, string>>({});

  return (
    <section className="mt-4 p-4 rounded border border-sky-500/30 bg-sky-950/20 max-w-md">
      <h4 className="text-sm font-semibold text-sky-300 mb-1">{form.title}</h4>
      <p className="text-xs text-zinc-400 mb-3">
        Add any details that would help with this request. Saving here does not submit a ticket.
      </p>

      {submitted ? (
        <>
          <p role="status" className="text-xs text-emerald-400 mb-3">
            Your details are saved in this page. No ticket has been submitted.
          </p>
          <dl className="space-y-2 text-xs">
            {form.fields.map((field) => (
              <div key={field.id}>
                <dt className="font-medium text-zinc-300">{field.label}</dt>
                <dd className="text-zinc-400 whitespace-pre-wrap">{values[field.id] || 'Not provided'}</dd>
              </div>
            ))}
          </dl>
        </>
      ) : (
        <form
          className="space-y-3"
          onSubmit={(event) => {
            event.preventDefault();
            setSubmitted(true);
          }}
        >
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
                  className="w-full min-h-20 text-xs p-2 rounded bg-zinc-950 border border-zinc-700 text-zinc-200"
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
                  className="w-full text-xs p-2 rounded bg-zinc-950 border border-zinc-700 text-zinc-200"
                />
              )}
              <p id={`${idPrefix}-${field.id}-description`} className="mt-1 text-[11px] text-zinc-500">
                {field.description}
              </p>
            </div>
          ))}
          <button
            type="submit"
            className="w-full py-2 bg-sky-700 hover:bg-sky-600 transition-colors rounded text-xs text-white font-semibold"
          >
            {form.submitLabel}
          </button>
        </form>
      )}
    </section>
  );
}
