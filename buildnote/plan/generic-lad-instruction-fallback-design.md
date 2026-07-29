# Generic LAD/FBD Instruction Fallback — Design

Date: 2026-07-29
Status: Approved (brainstorming session)
Scope: `src/Mcp.Knowledge/Parsing/ProgramBlockLogicYamlWriter.cs` + tests in `tests/Mcp.Knowledge.Tests`

## Problem

The FlgNet → SCL-like translator hardcodes every supported instruction:

- `EvaluatePartOutput` (line 1005) is a long chain of `if part.Name == "..."` branches.
- Family gates are hardcoded name lists: `IsArithmeticPart`, `IsFunctionExpressionPart` (line 1423), `IsInstanceCallPart` (line 1461), `IsControlFlowPart`, `IsProcedureFunctionPart`.
- Pin direction is hardcoded in `IsInputPin` (line 1254) / `IsOutputPin` (line 1312).

Any instruction not in these lists falls through to `Unsupported LAD/FBD part '{part.Name}'` and the network degrades to partial/untranslated — the logic is lost for downstream agent analysis.

## Goal

No instruction ever hard-fails translation. Unknown instructions render as a best-effort,
topology-derived SCL-like statement, marked `partial` with an explanatory note so the
downstream agent knows the trust level. Output for currently supported instructions must
not change (proven by the existing test suite passing unmodified).

## Decisions (from brainstorming)

- Unknown instructions get **generic best-effort rendering** (not an external catalog,
  not exact-only handlers).
- The file's "ported from PlcSourceExporter, keep changes minimal" constraint is
  **dropped** — mcp-knowledge is the home of this code; restructure freely.
- **Approach 1**: add the generic fallback and loosen the name-list gates. No registry
  refactor of the `EvaluatePartOutput` chain (deferred; pure structure, no capability).

## Design

### 1. Pin-role inference: topology first, conventions second

Replace the hardcoded pin-name tables with a rule order:

1. **Topology**: a pin recorded in `_inputSources` (wire target) or `_powerInputs` is an
   input. A pin that appears as a wire *source* is an output. This requires one addition
   in `FlgNetContext.Create`: collect a `_sourcePins` set during wire parsing (the
   information is computed at line 607 today but discarded).
2. **Conventions** (only when topology is silent, e.g. a pin wired straight to an
   operand access): `eno` and `out*` → output; everything else → input. This matches the
   current default for unlisted pins, so no regression.
3. `en` / `pre` remain special enable pins, excluded from bindings (unchanged).

`IsInputPin` / `IsOutputPin` shrink to these conventions; behavior for known parts is
preserved because topology decides first.

### 2. Generic renderer

New method `BuildGenericPartExpression(part, pinName, notes)` becomes the final branch of
`EvaluatePartOutput`, replacing the `Unsupported LAD/FBD part` dead end:

- `eno` output → the enable expression (same pattern as Move/calls today).
- Part has a non-empty `InstanceName` (from the `<Instance>` element — topology, not a
  name list) → output refs render as `InstanceName.PINNAME`, same as hardcoded TON/CTU.
- Otherwise → `PART_NAME(pin1 := value, pin2 := value, ...)` built from resolved input
  pins (reusing `GetInputBindings`). Flows into the existing direct-assignment path, so a
  wired output automatically becomes `target := PART_NAME(...);`.
- Every generic rendering adds a note:
  `Rendered '<name>' generically; pin semantics not verified.`
  → network status is `partial`, never `exact`.
- If nothing resolves (no inputs, no enable, no instance) → note + empty string (today's
  behavior). The fallback never throws.

### 3. Loosen the name-list gates

- `IsInstanceCallPart` (13-name list): replace the gate in `BuildPartCallStatements` and
  the instance-output branch of `EvaluatePartOutput` with "part has a non-empty
  `InstanceName`". Delete the list.
- `IsFunctionExpressionPart` (20-name list): its branch emits exactly `Name(bindings)`,
  identical to the generic renderer. Delete the branch and the list; the fallback absorbs
  it. (`IsProcedureFunctionPart` for RD_LOC_T/RD_SYS_T stays — genuinely special output
  handling.)

Untouched (exact and correct today): contacts/negation, compares, coils/latches,
Sr/Rs dominance, edge parts (PContact/NContact/PBox/NBox/PCoil), Move/S_Move, Calc,
InRange, control flow (Jump/Return*), Inc.

### 4. Testing

New tests in `tests/Mcp.Knowledge.Tests`, following existing writer-test patterns:

- Unlisted expression instruction (e.g. `SHR`, `NORM_X`) with wired in/out → assert
  `target := SHR(in := .., n := ..);`, status `partial`, generic-rendering note present.
- Unlisted instance FB (e.g. `CTD`) → assert `InstanceName(CD := .., PV := ..);` and
  `InstanceName.Q` output refs.
- Unknown part with `en` wired → assert `IF ... THEN ... END_IF;` guard wrapping.
- Regression: the full existing test suite passes unchanged.

## Error handling

The generic renderer is total: every path returns either a statement or a note, never an
exception. Malformed XML and missing FlgNet handling are unchanged. A network containing
at least one generically rendered part is reported as `partial`; fully bespoke networks
remain `exact`.

## Expected diff shape

`ProgramBlockLogicYamlWriter.cs`: −~80 lines of name lists, +~80 lines of generic
renderer + `_sourcePins` plumbing. No public API change
(`GetNetworkStatementTextByCompileUnitId` signature unchanged). No knowledge.db schema
change — statements/notes flow through the existing `logicStatements` property.

## Amendments (from implementation planning, 2026-07-29)

1. `IsFunctionExpressionPart` / `IsInstanceCallPart` lists are **kept** (not deleted as section 3
   proposed): they remain the exact, note-free path. The generic fallback handles only unlisted
   parts. Deleting the lists would reclassify known-exact instructions as generic.
2. The "rendered generically" signal surfaces as a leading `// Translated generically: ...`
   comment statement, because `GetNetworkStatementTextByCompileUnitId` returns only statements —
   notes/confidence never reach knowledge.db.
3. FlgNet `<Part>` XML carries no pin-direction info (verified against
   `exported/TestPLCExportDemo/`), so pin direction is inferred from wire topology plus
   conventions (`PinRole` enum), never from XML attributes.
