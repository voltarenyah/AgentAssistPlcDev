# 009. An oversized tool result keeps its shape instead of collapsing to field names

Status: pending
Created: 2026-10-06
Depends on: none

## Goal

When a tool result is larger than the agent's budget, the model still receives usable data. Field names
alone are never the whole of what it gets: the result is shrunk level by level, each level keeps its
first entries, each drop is counted, and the entries that survive still carry their identity (an `id`,
a `name`, a path) so the model can ask a narrower question.

Before (observed in a live conversation): `capture_source_evidence` returned
`{snapshot:{plcName, checksum, objects:[…774]}, candidates:[], candidateExports:[], isUntrackable:false}`.
Nothing in that shape is trimmable by the current passes — the two top-level arrays were empty and there
is no top-level string — so the compactor fell through to its last resort and the model received

```json
{"_truncated":true,"_omitted":true,"originalType":"object",
 "availableFields":["snapshot","candidates","candidateExports","isUntrackable"]}
```

No value, no count, no page. The conversation could not read the source object id it had asked for and
stalled; it had no way to tell a truncated-but-present field from an absent one.

After: the same result comes back bounded, with the objects' ids and names intact up to a stated limit,
and a count of what was dropped.

## Context

- `src/Agent/Chat/ToolResultCompactor.cs` is the only shrinker, called from
  `AgentLoop.ExecuteToolCallAsync` (`src/Agent/Chat/AgentLoop.cs:665`) with
  `ToolResultMaxChars` (default 8000, `src/Agent/Chat/ChatModels.cs:64`).
- Its three passes all look at top-level properties only:
  - `:38-45` trims a top-level string longer than 1200 characters;
  - `:47-54` trims a top-level array, and only while `array.Count() > 1`;
  - `:56-70` trims the longest top-level **string** property and `break`s when none is left, so an
    object whose bulk is a nested object or a nested array cannot be shrunk at all;
  - `:72-76` then hands the still-oversized node to `MinimalSummary` (`:108-118`), which keeps the key
    names and nothing else. `_omitted:true` there is a boolean flag, not the count of anything.
- The shapes that hit this are structural, not exotic: `SourceEvidenceCaptureResult`
  (`src/Contracts/Engineering/ManagedSourceEvidence.cs:98-104`) nests everything under `snapshot`
  (`SourceEvidenceSnapshot`, `:79-84`), and a snapshot of one PLC is 774 objects on the device used for
  the observation above.
- `:49` and `:82` re-serialize the entire node on every loop iteration (`obj.ToJsonString()`), so
  trimming an N-element top-level array costs O(N²) serialization — a top-level array of a few hundred
  entries is seconds of CPU on the request path.
- `_truncated` is a contract, not a detail: `src/Agent/Chat/SystemPrompt.cs:31` instructs the model to
  page when it sees it, and `tests/Agent.Tests/SystemPromptTests.cs:52` asserts the prompt mentions it.
- Behaviour pinned today: `tests/Agent.Tests/AgentLoopTests.cs:337,374,653` (assert `_truncated`) and
  `:654` (assert the content stays within `ToolResultMaxChars`).

## Constraints

- The returned string must never exceed `maxChars` and must always be valid JSON; values are never cut
  mid-token (`ToolResultCompactor.cs:6-7`).
- `_truncated` keeps its meaning and stays truthful: `true` only when something was actually dropped,
  and the drop is reported as a count (`_omitted`), not just a flag.
- Keep the false-signal-free property the current summary was reaching for: a field whose value was
  dropped must not look like a field that was empty.
- Do not change any tool's result contract, any tool, or the budget itself; this is the shared feedback
  path of every tool call.
- Preserve the existing caller contract: `Compact(JsonElement, int) -> string`, no new dependency.

## Done when

1. A test proves an oversized **nested** result keeps the identity fields of its first entries (for the
   capture shape: `snapshot.objects[0].id`) and reports an omitted count.
2. A test proves the returned string is ≤ `maxChars` and parses as JSON for each of: a nested object, a
   top-level array, a top-level long string, and a scalar.
3. A test proves a result that already fits is returned byte-identical (no `_truncated` added).
4. A test proves a nested array is trimmed without re-serializing the whole node per element (a large
   top-level array completes well inside a generous time bound, or the implementation no longer
   serializes inside the loop).
5. `dotnet test tests/Agent.Tests` and `dotnet test tests/ApiHost.Tests` pass.

## Evidence

<pending>
