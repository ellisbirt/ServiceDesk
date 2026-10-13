# Claude system-prompt evals

Use this folder to develop a system prompt and check that changes improve the
assistant's behavior without breaking existing expectations. The files are
intentionally independent of an application language or Claude SDK.

## Files

- `system-prompt.md` — the prompt under development. Copy it into your
  application's Claude configuration when you are ready.
- `cases.json` — representative conversations and the behaviors expected from
  the assistant. Keep cases focused and give each one a stable ID.
- `rubric.md` — shared scoring criteria for reviewing model responses.

## Eval workflow

1. Edit `system-prompt.md`.
2. For every case in `cases.json`, send its `input` to Claude with the draft
   system prompt. Use the same model and settings when comparing prompt
   versions.
3. Record each response and score it against `rubric.md`. A case passes only
   when all of its `must` checks pass and its overall score meets the rubric's
   pass threshold.
4. Review failures, update the prompt or cases, and run the full set again.
   Keep cases that caught regressions; do not remove a case just to make a
   prompt version pass.
5. Record the prompt version, Claude model, date, and results alongside the
   run in your team's preferred tracking system. Avoid putting secrets or
   sensitive user data in eval files.

These starter cases are examples, not a complete specification. Replace the
placeholder behavior with the requirements of your application before relying
on eval results.
