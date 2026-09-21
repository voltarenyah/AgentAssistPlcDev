# ADR-0004: Design Token Authority for the Notion-kit Migration

## Status

Accepted

## Context

`@notion-kit/ui@1.2.0` is installed and its stylesheet is imported at `studio/src/assets/main.css:2`.
`docs/STYLEGUIDE.md` designates the Notion design analysis as the canonical design guide and states
that it replaces the former local Studio visual and component rules, so the intended design language
is Notion's. `studio/AGENTS.md` records the library inventory and the import path.

Studio still owns a parallel primitive set: 28 components under `studio/src/components/ui`, imported
by 36 files out of 95 non-test TSX components. notion-kit's `primitives` barrel exports 251 symbols,
covering 20 of those 28. The eight with no counterpart are `accordion`, `button-group`,
`collapsible`, `color-picker`, `hover-card`, `slider`, `toggle`, and `toggle-group`; they are not
hypothetical — Studio imports `toggle-group` in 3 files, `toggle` in 2, and `slider` in 2.

The two systems declare the same token names with opposite meanings:

| Token | Studio meaning | notion-kit meaning (dark) |
|---|---|---|
| `--muted` | surface `#262626` | text `rgba(255, 255, 255, 0.3)` |
| `--secondary` | surface `#262626` | text `#a8a49c` |
| `--primary` | foreground `#e5e5e5` | foreground `#f0efed` |
| `--border` | `rgb(255 255 255 / 0.07)` | `rgba(255, 255, 255, 0.1)` |
| `--radius` | `0.625rem` | `0.5rem` |

Studio's `:root` and `.dark` blocks load after the notion-kit stylesheet, so Studio currently wins
for every shared name. Measured colliding call sites in Studio source, counting each name's total
minus its `-foreground` variant (which does not collide):

| Token | Colliding call sites in Studio |
|---|---|
| `--muted` | 81 (`bg-muted` 80, `text-muted` 1) |
| `--secondary` | 6 (`bg-secondary` 5, `text-secondary` 1) |
| `--primary` | 32 |
| `--border` | 25 |
| `--radius` | every rounded corner |

`text-muted` alone totals 436 occurrences, but 435 of those are `text-muted-foreground`, a
Studio-only name that does not collide. Reading that number without the suffix would overstate the
blast radius by roughly 400×.

`.notion-kit-surface` in `studio/src/assets/main.css` restores notion-kit's values inside a scope so
the component catalog preview renders faithfully. It is a preview device: it exists only because the
shared names disagree, and it cannot be applied to 36 production files.

Constraint: 83 colocated tests run under happy-dom, which has no layout engine, so they cannot
detect a visual regression caused by a token change. No screenshot baseline exists.

## Decision Point

- **Question**: Which token system is authoritative for the names both systems declare, once
  notion-kit becomes the primitive layer?
- **Why a decision exists**: at least two credible, materially distinct options are supported by
  repository evidence — keep Studio's token semantics and adapt notion-kit per component, or let
  notion-kit's semantics win and migrate Studio's usages. They produce different durable token
  vocabularies, and the choice cannot be revisited cheaply once components are written against
  either one.
- **Scope boundary**: authority over the shared token names and the surface semantics they carry.
  Not the component-by-component visual redesign, not the fate of the eight primitives with no
  counterpart, and not whether notion-kit's `ThemeProvider`/`Toaster` replace Studio's theme module
  and toast wrapper.

## Decision

notion-kit's values become authoritative for the shared token names. Studio's two conflicting
*surface* semantics are renamed out of the collision before the switch, so the switch itself changes
no pixels:

1. Add Studio surface tokens that do not collide — `--surface-muted` and `--surface-secondary`,
   carrying today's Studio values (`#f5f5f5` light, `#262626` dark) — and map them in `@theme inline`
   as `--color-surface-muted` and `--color-surface-secondary`.
2. Rename the colliding Studio usages: `bg-muted` → `bg-surface-muted` (80 sites across 37 files),
   `bg-secondary` → `bg-surface-secondary` (5), and resolve the single `text-muted` and single
   `text-secondary` site per site.
3. Remove Studio's declarations of `--muted` and `--secondary` so notion-kit's values apply
   app-wide, and let `--primary`, `--border` and `--radius` take notion-kit's values.
4. Delete `.notion-kit-surface` and its use on the catalog preview page: the collision it worked
   around no longer exists.

Three values deviate deliberately, because notion-kit's value for them is wrong for this application.
Contrast figures below are sampled from rendered pixels, not from computed styles.

- `--muted` is `#737373` in light and `#807d78` in dark, instead of notion-kit's `#46444073` /
  `rgba(255, 255, 255, 0.3)`. notion-kit's values measure 1.05:1 and 2.61:1 against Studio's page
  backgrounds, below WCAG AA, and Studio's captions and notion-kit's descriptions both read this
  token. One value cannot serve both themes: a grey light enough for `#0a0a0a` is too light for
  `#ffffff`. The chosen values measure 4.74:1 and 4.83:1; the dark value is notion-kit's own
  `--icon` colour, so it stays inside the library palette.
- `--secondary` is `#6f6c67` in light, instead of notion-kit's `#8e8b86`, which measures 3.39:1 on
  white. The chosen value measures 5.23:1. Dark keeps notion-kit's own `#a8a49c` (7.97:1), which
  already passes — but it must be restated in Studio's `.dark` block, because a declaration in the
  `:root` block silently wins over notion-kit's `.dark` rule (equal specificity, and this file loads
  last).
- `--ring` stays Studio's. The two systems use that name for different jobs: Studio draws focus
  rings with it, while notion-kit uses it for a subtle inset field ring. notion-kit's dark value is
  7% white, so adopting it would make keyboard focus nearly invisible on every control.

One residual miss is accepted rather than fixed here: notion-kit's grey badge in light mode renders
`#6f6c67` on its own `#cecdca`-at-50% chip, which samples at 4.18:1, about 7% under AA. That is the
library's own light-mode chip design and it applies to one status badge; the pre-change state was
1.05:1, so this is a large improvement, and the correct place to resolve it is the badge's own
surface migration rather than by darkening a global text token further.

### Decision Details

| Item | Content |
|---|---|
| **Decision** | notion-kit owns the shared token names; Studio's conflicting surface semantics move to `--surface-muted` and `--surface-secondary`; the `.notion-kit-surface` workaround is retired. |
| **Why this** | It yields one token vocabulary instead of two, lets notion-kit primitives render as designed without per-component text overrides, and keeps the switch itself verifiable because the rename is behaviour-preserving. |
| **Known unknowns** | The two stray `text-muted`/`text-secondary` sites need per-site judgment. Whether the global `--radius` shrink of 2px is acceptable needs a visual pass; the foundation change is otherwise pixel-neutral, so that shrink is the one broad visual delta to review. |
| **Reconsider when** | The Notion design analysis stops being the canonical guide, or the pilot shows notion-kit's token semantics cannot express a Studio surface need without per-component overrides — which would falsify the central premise of this option. |

## Rationale

The deciding evidence is the STYLEGUIDE authority chain plus the goal of a redesign rather than a
component swap. `docs/STYLEGUIDE.md` states the Notion design analysis replaces the former local
Studio visual rules, so keeping Studio's token semantics as the app-wide authority contradicts the
documented direction. Option A also cannot deliver a redesign: it leaves two vocabularies standing
(435 Studio-only `text-muted-foreground` occurrences plus 80 `bg-muted` surfaces in Studio terms,
against notion-kit components that speak `text-muted` and `bg-default/5`), and every notion-kit
component would need a wrapper that fights the library's own internals — such as `CardDescription`'s
hard-coded `text-muted`, which cannot be fixed by a parent scope without also changing Studio's
surfaces inside that scope.

The choice is affordable because the collision is narrower than it first appears: only `--muted` and
`--secondary` carry *conflicting* semantics, and both are surfaces in Studio. Renaming those 87
sites to values that preserve today's pixels reduces the switch to a mechanical, reviewable diff,
after which the visual redesign proceeds surface by surface against a single vocabulary.

### Options Considered

| Option | Requirement and repository fit | Current-scope benefit | Lifecycle cost | Maintainability | Material trade-offs |
|---|---|---|---|---|---|
| **A. Studio tokens authoritative, adapt notion-kit per component** | Fits the 36 importing files and 83 tests unchanged; contradicts `docs/STYLEGUIDE.md`'s authority statement | No token migration; no visual delta | Permanent translation layer; ~20 wrapper components; `CardDescription`-class internals need per-component overrides | Two vocabularies coexist indefinitely; each new notion-kit component needs a new wrapper | Does not deliver a redesign; keeps `.notion-kit-surface`-style adaptation forever; the translation layer must be re-audited on every library upgrade |
| **B. notion-kit tokens authoritative, rename Studio surface semantics (selected)** | Matches the STYLEGUIDE authority chain and the redesign goal; notion-kit's `.dark` selectors already match Studio's existing `dark` class on `<html>` | One vocabulary; notion-kit components need no per-component text fixes; retires `.notion-kit-surface` | 87 mechanical renames across 37 files in one commit; global 2px radius change; adopts notion-kit's low-contrast `--muted` | Single vocabulary; new components need no translation; the collision cannot recur | Touches 37 files at once (mitigated by the rename being mechanical); requires a browser visual pass because tests cannot see colour |
| **C. Scope notion-kit tokens per migrated surface (keep `.notion-kit-surface`)** | Extends the current preview mechanism to production surfaces | Incremental, no global change | Every surface wrapped; Studio-styled children inside a wrapped subtree silently inherit notion-kit values | Two token systems live in one DOM tree at differing depths — the hardest failure mode to reason about | Rejected: the scoping bug that made descriptions invisible on the catalog page would reappear at every surface boundary |

**Selected**: Option B. It is the smallest option that satisfies the STYLEGUIDE authority chain and
the redesign goal, and its cost is a mechanical rename rather than a semantic rewrite.

## Consequences

### Positive Consequences

- One token vocabulary for the whole application; the collision cannot recur in future surfaces.
- notion-kit primitives render as designed without per-component colour overrides, so adopting
  further components is a matter of importing them.
- `.notion-kit-surface` retires, removing a workaround that would otherwise become permanent.
- The rename is behaviour-preserving, so the token switch can be verified by comparing rendered
  surfaces before and after, not by trusting the diff.

### Negative Consequences

- 87 call sites across 37 files change in one commit, which is review noise; it is deliberately one
  commit so that no state mixes both vocabularies.
- `--radius` moving from `0.625rem` to `0.5rem` shrinks every rounded corner by 2px, and because
  `@theme inline` derives `--radius-sm/-md/-lg` from it while notion-kit's compiled utilities compute
  `calc(var(--radius) ± px)`, both scales shift. This is the one broad visual delta the foundation
  change carries, so it needs an explicit visual pass.
- `--muted` and `--ring` deviate from notion-kit's values for accessibility reasons recorded above.
  Both deviations must be re-checked on a library upgrade, because a future notion-kit release could
  change how those names are used.
- The two stray `text-muted`/`text-secondary` sites require judgment, so the rename is not fully
  mechanical.

### Neutral Consequences

- `text-muted-foreground` and `--muted-foreground` remain Studio-only names and keep working; they
  are not part of this decision and may be retired later for vocabulary consistency.
- notion-kit declares roughly 20 additional tokens at `:root`/`.dark` (`--bg-sidebar`, `--icon`,
  `--tooltip-*`, `--default`, `--base`, and others) that are already inherited app-wide today. This
  decision makes that inheritance intentional rather than incidental.
- The eight primitives with no notion-kit counterpart stay in `studio/src/components/ui` and move to
  the new tokens with everything else.

## Architecture Impact

- `studio/src/assets/main.css`: shared names take notion-kit's values; two Studio surface tokens are
  added; `.notion-kit-surface` is removed.
- `studio/src/catalog/pages/NotionKitPage.tsx`: the `notion-kit-surface` class is removed.
- 37 files under `studio/src/**` are renamed from `bg-muted`/`bg-secondary` to the surface tokens.
- No dependency is added or removed; no API, protocol, persisted format, or schema changes.
- `docs/ui-spec/studio-ui-library-integration-ui-spec.md` is **superseded** for its primitive-reuse
  rule, which currently requires reusing the existing Orca primitives and excludes new runtime
  dependencies. Leaving it in force would give the repository two contradictory UI authorities.
- Reversibility is high: both token sets remain in git history, the rename is mechanical, and no
  data or contract depends on the outcome.

## Implementation Guidance

- Land the rename and the token switch in **one** commit so no intermediate state mixes vocabularies.
- Verify in the browser, not in happy-dom: capture a dense surface in both themes before and after,
  and compare proportion, overflow, and contrast. The automated suite cannot see layout or colour.
- Keep the eight primitives without a notion-kit counterpart on the new tokens; do not delete them
  in this change.
- Do not adopt notion-kit's `ThemeProvider`, `ThemeToggle`, or `Toaster` here. Studio's theme module
  already toggles the `dark` class on `<html>`, which is what notion-kit's dark selectors rely on,
  and Studio's toast wrapper already wraps sonner.
- Resolve the `--muted` contrast question before converting text-heavy surfaces, so the decision is
  made once rather than per surface.

## Related Information

- `docs/STYLEGUIDE.md` — canonical design guide and the library import rules.
- `docs/ui-spec/notion-kit-ui-migration-ui-spec.md` — the migration slice this decision governs.
- `docs/ui-spec/studio-ui-library-integration-ui-spec.md` — superseded primitive-reuse rule.
- `studio/AGENTS.md` — component inventory, import subpaths, registry status.
- Issue #109 — the missing general device-selection entry point, which constrains pilot choice.
- Commits `efb6d8f` (catalog rendering fix that introduced `.notion-kit-surface`), `9407509`
  (restored six stale MainStudio assertions).
