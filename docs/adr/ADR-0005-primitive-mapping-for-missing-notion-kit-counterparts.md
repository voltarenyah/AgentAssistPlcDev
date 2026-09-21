# ADR-0005: Mapping Studio Primitives notion-kit Does Not Provide 1:1

## Status

Proposed

## Context

The notion-kit migration is complete for every surface whose Studio primitives map
one-to-one. Two mapping gaps remain, and both block real product surfaces:

**Gap 1 — `ToggleGroup`.** notion-kit has no `ToggleGroup` at all. Studio uses it in two
places, both single-select segmented controls:

| Site | Options | Chrome |
|---|---|---|
| `CreateWorkbenchDialog.tsx:166` | Attach to running TIA / Open project file (.ap17) | Hand-rolled sliding indicator: an absolutely positioned span translated by `translate-x-full`, sitting behind transparent `ToggleGroupItem`s |
| `OperationTimingList.tsx:178` | Workflow / Source export | Plain two-option switch |

**Gap 2 — inline `Command`.** `TagFilter.tsx` (131 lines, 9 tests) and `TagPicker.tsx` (81 lines, 3 tests)
build inline, non-modal filterable lists on cmdk. notion-kit's `Command` is documented as *"a
dialog-only command palette built on the autocomplete primitive"*, so it is not a drop-in
replacement. However, notion-kit also exports `Autocomplete` (an inline Base UI autocomplete taking
`items`) and `Combobox` (documented as *"a Base UI combobox with Notion-style chip input, floating and
inline variants, and creatable items"*). Those are inline and cover this ground, which means these two
surfaces are a different interaction shape rather than an outright blocker.

Supporting evidence from the migration already done: notion-kit's `Tabs`, `TabsList` and `TabsTrigger`
were adopted successfully for the worktree tab strip in commit `dcaec73`, verified in the browser as
two ARIA tabs with a working selected state.

## Decision Point

- **Question**: How should the two primitives notion-kit does not provide 1:1 be represented —
  replace them with the nearest notion-kit primitive, or keep the Studio primitive?
- **Why a decision exists**: at least two credible options exist for each gap, with materially
  different costs. For `ToggleGroup`, notion-kit's `Tabs` is the nearest primitive but changes the
  chrome; for `Command`, `Combobox`/`Autocomplete` match the interaction but change the component
  shape and therefore the surface's UX and tests. Neither is a mechanical swap.
- **Scope boundary**: the two `ToggleGroup` sites and the two inline-command surfaces. Not the eight
  primitives with no counterpart at all (`Slider`, `accordion`, `button-group`, `collapsible`,
  `color-picker`, `hover-card`, `toggle`, `toggle-group`), which stay on Studio primitives.

The two gaps are documented together because they are decided in one review, but they are
independent: either can be revisited without disturbing the other.

## Decision

**Gap 1 — adopt notion-kit `Tabs` for both `ToggleGroup` sites, and drop the hand-rolled sliding
indicator.** `Tabs` is a single-select ARIA control, is already proven in this codebase, and removes
custom chrome rather than reproducing it. The `CreateWorkbenchDialog` mode switch keeps its two
labels and its `mode` state; only the control and the indicator change.

**Gap 2 — `TagPicker` uses notion-kit's `TagsInput`; `TagFilter` keeps a filterable list and takes
`Autocomplete`.** `@notion-kit/ui/tags-input` ships a controlled chip input — `value: { tags, input }`,
`onTagsChange`, `onInputChange`, an optional zod `inputSchema`, and `TagOption { value, color }` — which
is the tag picker's exact job, down to the colour that Studio's tags already carry. The tag filter
remains a filterable list over existing tags with no creation, which `Autocomplete` covers. These are
interaction-shape changes, so each is its own slice with its own UI Spec update and tests, not a
primitive swap folded into a broader commit.

`@notion-kit/ui/selectable` was checked as a candidate for this gap and for the ten native `<select>`
elements, and is **not** one: it is a rubber-band marquee selection container (`selectionRect`,
`selectionMode`, `activationConstraint`, `onSelectStart`/`Move`/`End`) for canvas-style surfaces, not a
row or dropdown primitive. Studio has no surface that needs marquee selection, so the native selects
still map to Base UI's `Select` with an `items` collection.

### Decision Details

| Item | Content |
|---|---|
| **Decision** | Both `ToggleGroup` sites move to notion-kit `Tabs`; `TagPicker` moves to `Combobox` and `TagFilter` to `Autocomplete`. |
| **Why this** | It removes the last two Studio primitives that block real product surfaces, and each target is the library's documented primitive for that interaction rather than an approximation. |
| **Known unknowns** | The tag filter's `Autocomplete` mapping still needs design work, since it is a filterable list rather than a chip input. `TagsInput` is controlled on both tags and the draft input, so `TagPicker`'s state shape changes from one array to that pair, and its tests move with it. |
| **Reconsider when** | `Combobox`/`Autocomplete` cannot express the tag filter's current behaviour without losing function, or the tag surfaces are judged not worth the redesign; in that case keep cmdk and record those two surfaces as permanently on Studio primitives. |

## Rationale

For `ToggleGroup` the choice is easy to justify: `Tabs` is semantically the same thing (single
selection from a small set), it is already proven in this repository, and the alternative — keeping
`ToggleGroup` — means keeping a Studio primitive solely for two call sites while the ADR-0004 goal is
one vocabulary. The sliding indicator deserves specific mention: it is not a library feature but
hand-written chrome that duplicates an active state `Tabs` already renders, so removing it reduces
code and removes a custom animation to maintain.

For the command surfaces, the evidence changed the conclusion. The earlier position in the migration
UI Spec was that notion-kit's dialog-only `Command` blocks these two files. Reading notion-kit's
exports shows `Autocomplete` is inline and `Combobox` is a chip multi-select with creatable items,
which is the tag picker's exact use case. The cost is therefore design and test work, not a dead end.
Option C below — keep cmdk permanently — remains defensible if the redesign is not worth it, and is
the fallback rather than the default.

### Options Considered

| Option | Requirement and repository fit | Current-scope benefit | Lifecycle cost | Maintainability | Material trade-offs |
|---|---|---|---|---|---|
| **A. Adopt `Tabs` for both `ToggleGroup` sites; `Combobox`/`Autocomplete` for the tag surfaces (selected)** | `Tabs` already proven in `dcaec73`; `Combobox` documents chip multi-select with creation, which is the tag picker's job | Removes the last blocking Studio primitives; deletes hand-rolled indicator chrome | Two focused slices; the tag surfaces need real design and test rewrites | One vocabulary; no Studio primitive kept alive for two call sites | The tag surfaces change interaction shape, so their behaviour and tests change with them |
| **B. Keep `ToggleGroup` and cmdk on Studio primitives** | Zero new work; both already work | No design risk | Keeps two Studio primitives alive solely for four call sites; the migration can never be declared complete | Two vocabularies persist exactly where the ADR-0004 rationale said they must not | Directly contradicts the reason ADR-0004 chose notion-kit as the foundation |
| **C. Hybrid: `Tabs` for `ToggleGroup`, keep cmdk for the tag surfaces** | `Tabs` proven; the tag redesign deferred | Unblocks `CreateWorkbenchDialog` and `OperationTimingList` at low risk | Leaves two surfaces on Studio primitives indefinitely | Acceptable if recorded as a decision rather than an omission | Partial migration survives, but only two files, and it is explicit |

**Selected**: Option A, with Option C as the recorded fallback if the tag redesign proves not worth it.

## Consequences

### Positive Consequences

- No Studio primitive remains in the product surface set except those with no counterpart at all.
- The hand-rolled sliding indicator is deleted rather than ported.
- The tag picker gains the library's chip + creatable behaviour, which is more capable than the
  current inline list.

### Negative Consequences

- The tag surfaces change interaction shape, so their 12 combined tests do not carry over; they need
  rewriting against the new behaviour rather than retargeting assertions.
- `Combobox`/`Autocomplete` need `items` collections and accessors, which is real work for data that
  is currently a plain array.
- `Tabs` renders its own active treatment, so `CreateWorkbenchDialog`'s mode switch will look
  different from today; that is intended but is a visible change on a creation dialog.

### Neutral Consequences

- The eight primitives with no counterpart remain on Studio primitives and are unaffected.
- `catalog/pages/FormPage.tsx` continues to preview Studio's `ToggleGroup`; that page demonstrates
  Studio's own set and is not migrated.

## Architecture Impact

- `CreateWorkbenchDialog.tsx` and `OperationTimingList.tsx` drop their `@/components/ui/toggle-group`
  import; `CreateWorkbenchDialog` also loses its sliding-indicator span.
- `TagPicker.tsx` and `TagFilter.tsx` stop importing `@/components/ui/command` and move to
  notion-kit's `Combobox`/`Autocomplete`.
- No dependency is added; cmdk remains in the stack for any other consumer and is not removed.
- No API, protocol or persisted-format change.

## Implementation Guidance

- Land the `ToggleGroup` change first: two files, low risk, no behaviour change beyond chrome.
- Treat each tag surface as its own slice with a UI Spec update, because the interaction changes.
- Verify each in the browser in both themes; the tag surfaces are reachable from the project landing
  page, so they can be exercised without the device-selection path that issue #109 blocks.
- Do not remove cmdk from the dependency list as part of this work.

## Related Information

- `docs/adr/ADR-0004-notion-kit-design-token-authority.md` — the foundation decision this completes.
- `docs/ui-spec/notion-kit-ui-migration-ui-spec.md` — migration order, which lists these surfaces as
  decision-gated.
- `studio/AGENTS.md` — component inventory and import paths.
- Commit `dcaec73` — the `Tabs` migration that provides the precedent.
- Issue #109 — blocks the device surfaces, which are the only other unfinished area.
