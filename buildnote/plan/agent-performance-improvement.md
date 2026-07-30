# Plan: Agent query performance improvements

Source: analysis of session export `analyze how _Cav_A_.Cavity.CAB.PLS_Green_Cup.CAB was process.md`
(1 user question → 12 API rounds, 218,866 prompt tokens, hard-stopped with no answer),
plus reviews of the `MaxRounds` rule and context-size accounting.

## Problem summary

1. **Knowledge DB gaps (root cause).** READS/WRITES edges are silently dropped for
   accesses the importer can't classify (`Access == "unknown"`), and struct-member
   `Variable` nodes (`symbol:Cav_A.Cavity.CAB.PLS_Green_Cup.CAB`) are disconnected
   from `DB Member` nodes (`db-member:Cav_A:Cavity.CAB.PLS_Green_Cup`). The natural
   "where is X read/written" query returns 0 rows; the agent burns rounds guessing
   node IDs. Evidence: `get_network` for `030_Status_Management_Cav_A:18` reports
   `"writes": []` while its logic text clearly writes `PLS_Green_Cup.CAB`.
2. **Wasted rounds.** Speculative parallel searches, unneeded `get_schema`, guessed
   network fetches, failed SQL retries — ~6 of 12 rounds produced no usable evidence.
3. **Bad failure mode.** At round 12 the pending tool calls are discarded and a
   dead-end string is returned as the answer and persisted in history.
4. **No cost controls.** No token budget, no compaction, no context caching, no
   context-size visibility — usage is recorded per round and never acted on.

## Work items, prioritized

### P0 — fix the root cause and the worst failure mode

- [x] **I1. Importer: stop dropping unclassified accesses.**
  `src/Mcp.Knowledge/Graph/SemanticPlcGraph.cs:360-385` +
  `src/Mcp.Knowledge/Parsing/ProgramSemanticReference.cs:546-594`
  (`GetStandaloneAccessName`). For references with `Access == "unknown"`/`""`,
  emit a best-effort edge instead of none: infer direction from assignment/move
  target-vs-source position where possible; otherwise default to `READS`
  (over-inclusive beats missing). Extend the recognized LAD part/pin list
  (move boxes, assignments, SCL-style accesses) as cases surface.
  Verify: new tests in `tests/Mcp.Knowledge.Tests` importing a network with an
  unclassified access; assert the edge exists with the expected direction.
  Requires knowledge DB rebuild to take effect.

- [x] **I2. Importer: link struct-member Variables to DB Members.**
  `SemanticPlcGraph.cs:546-571` (`ImportDbMember`, id `dbmember:{db}.{path}`)
  vs `:688-692` (`SymbolId`, id `symbol:{dotted-name}`). Add a normalization pass
  that maps a dotted symbol path onto the matching `DB Member` chain and links
  them (e.g. `REFERS_TO` edge, or point READS/WRITES at the DB member directly),
  so ID guessing (`symbol:` vs `db-member:`) can no longer strand a query.
  Verify: test that `Cav_A.Cavity.CAB.PLS_Green_Cup.CAB` resolves to the
  `db-member:Cav_A:Cavity.CAB.PLS_Green_Cup(.CAB)` chain; rebuild DB.

- [x] **I3. Graceful final round instead of hard stop.**
  `src/Agent/Chat/AgentLoop.cs:105-110`. At `round >= MaxRounds`: execute the
  pending tool calls, then make one final API call with no tools offered and an
  injected instruction ("tool budget exhausted — answer with what you have, state
  what's unverified"). Return that partial answer. Do **not** persist the stop
  note as a normal assistant message in history.
  Verify: `tests/Agent.Tests` — loop that always requests tools ends with a real
  final-answer call, no orphan tool calls, history not poisoned.

### P1 — make the common query shape robust and cheap

- [x] **I4. New `get_variable_usage` knowledge tool.**
  `src/Mcp.Knowledge/Tools/KnowledgeTools.cs`. Input: `dbPath`, variable path.
  One call returns: networks whose `logicStatements` match the variable (LIKE,
  text is authoritative) UNION any READS/WRITES edges, labeled by direction, with
  block/network ids. Robust against remaining edge gaps; collapses the
  search → query → get_network chain into one call.
  Verify: tool test over a fixture DB; the `PLS_Green_Cup.CAB` case returns all
  7 networks from the traced session.

- [x] **I5. System prompt: add recipes and recovery rules.**
  `src/Agent/Chat/SystemPrompt.cs:9-32`.
  (a) Variable-lifecycle recipe: exact path known → `search` on leaf name →
  `get_variable_usage` (or one SQL over `logicStatements LIKE` + READS/WRITES) →
  `get_network` only for cited networks. Target ≤4 calls.
  (b) Empty-result recovery: 0 rows from a graph query → fall back to
  `logicStatements LIKE %name%` before guessing node IDs; edges may be missing,
  text is authoritative.
  (c) Discourage broad parallel searches (e.g. `kind=Variable` on a DB name)
  when an exact path is given.
  Verify: replay the traced question against a rebuilt DB; expect ≤5 rounds and
  a final cited answer.

- [x] **I6. Expose current context size.**
  `roundUsages` already holds exact per-round `PromptTokens`
  (`AgentLoop.cs:90-96`). Surface the last value (e.g. "context: 22.7k / 128k")
  in the UI, and treat the exporter's summed prompt tokens
  (`ChatSessionExporter.cs:98`) as "cumulative billed input", labeled as such.
  Verify: UI shows the last round's prompt tokens after each turn.

### P2 — cost controls and round economics

- [x] **I7. Prompt-token budget + compaction.**
  Cumulative prompt-token ceiling as a second guard alongside `MaxRounds`
  (usage already recorded, `UsageInfo` in `ChatModels.cs:37`). On crossing a
  threshold: shrink old tool results (already capped at `ToolResultMaxChars`
  8000, `AgentLoop.cs:15`) to their head, or warn the user.
  Verify: synthetic long session triggers compaction/warning before the API
  rejects the request.

- [x] **I8. Pre-send context estimation.**
  Estimate request size before calling (tokenizer approximation, e.g. SharpToken,
  ±10–20%; include tool JSON and `reasoning_content`). Used for the budget guard
  and a "near context window" warning — the API only reports usage after billing.
  Verify: unit test estimate vs actual `PromptTokens` within tolerance.

- [x] **I9. `get_schema` weight reduction.**
  `KnowledgeTools.cs:33-41` returns full DDL + all example queries every call
  (~2.5k tokens). Add a version hash so the agent can skip refetching, and/or
  move the static schema into the tool description.
  Verify: schema payload fetched at most once per session.

- [x] **I10. `get_network` compact mode.**
  Skip the repeated block-metadata wrapper (~150 tokens × N calls) via a
  `compact` argument or slimmer default shape.
  Verify: existing tests updated; compact response keeps block/network ids.

- [x] **I11. DeepSeek context caching + cache metrics.**
  Enable provider-side context caching if supported by `DeepSeekClient`; keep
  the request prefix byte-stable (it already is within a turn). Move the
  volatile runtime-context block to the **end** of the system message
  (`SystemPrompt.Build`, `AgentLoop.cs:165-176`) so the static rules prefix
  stays cacheable across turns. Parse cache hit/miss fields into `UsageInfo`
  (`ChatModels.cs:37`) — `prompt_tokens` reports full size regardless of cache.
  Verify: usage logs show cache-hit tokens on multi-round turns.

- [x] **I12. "Continue" affordance after round cap.**
  When the cap triggers, offer the user N more rounds instead of a dead end.
  Depends on I3 (cap becomes rare and non-fatal). Verify: UI flow test.

## Suggested order

1. I3 (small, kills the worst UX) → 2. I1 + I2 (root cause; needs DB rebuild)
→ 3. I4 + I5 (robustness and prompt guidance) → 4. I6 (visibility, trivial)
→ 5. I7/I8 (budget guard) → 6. I9/I10/I11 (token weight) → 7. I12.

## Acceptance

Replay the exact question from the traced session
("analyze how `Cav_A.Cavity.CAB.PLS_Green_Cup.CAB` was processed") against a
rebuilt knowledge DB: expect ≤5 rounds, ≤~30k cumulative prompt tokens, a final
answer citing all 7 networks (030_Status_Management_Cav_A:2,18;
034_Exhange_Supply_Evac_Cav_A:16,46,50; 030_Faults_Cav_A:6,8), and no
"Stopped after 12 rounds" outcome even when the cap is forced.
