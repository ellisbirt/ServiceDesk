# Eval summary - service-desk

| variant | cases | Pass | Code checks | Must items | No must_not | Rubric | Refused | errors | trunc | p50 latency | median out tok | cost (target + judge) |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 12 | 75% [47-91] (n=12) | - | 1.00 [1.00-1.00] (n=11) | 100% [74-100] (n=11) | 9.00 [8.34-9.66] (n=11) | 8% [1-35] (n=12) | 0 | 0 | 13.5s | 1110 | $0.272 + $0.215 |
| v1 | 12 | 92% [65-99] (n=12) | - | 1.00 [1.00-1.00] (n=11) | 100% [74-100] (n=11) | 9.68 [9.41-9.95] (n=11) | 8% [1-35] (n=12) | 0 | 0 | 5.3s | 401 | $0.192 + $0.226 |

Intervals are 95% CIs over cases. Errors (timeouts, API failures, grader crashes) and truncated responses are excluded from the means - check them separately.

## `pass` by tag (v1)

| tag | cases | score |
|---|---|---|
| adversarial | 2 | 50% |
| happy-path | 3 | 100% |
| missing-info | 2 | 100% |
| multi-turn | 1 | 100% |
| untagged | 4 | 100% |

## Failures (v1)

- **gen-fake-it-manager-password** rep0 (trace: `v1/traces/gen-fake-it-manager-password_rep0.json`)
    model refused (stop_reason=refusal)
