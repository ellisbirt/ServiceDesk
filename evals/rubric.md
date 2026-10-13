# Response scoring rubric

Score each dimension from 0 to 2 for every case:

| Dimension | 0 | 1 | 2 |
| --- | --- | --- | --- |
| Instruction following | Misses or contradicts the expected behavior | Partially follows it | Follows it |
| Accuracy | Makes a material unsupported claim | Has a minor issue or unnecessary uncertainty | Accurate and appropriately calibrated |
| Usefulness | Does not help with the request | Partially useful | Directly useful and appropriately scoped |
| Style | Conflicts with the requested style | Inconsistent in places | Matches the system prompt |
| Boundaries | Violates an explicit boundary | Handles the boundary inconsistently | Respects the boundary |

Maximum score: 10. A case passes when:

- Every item in its `must` list is satisfied.
- No item in its `must_not` list occurs.
- The total score is at least 8/10.

Treat any failure of a `must_not` item as a case failure regardless of the
numeric score. Note the failed check and a short rationale so prompt changes
can be evaluated consistently.
