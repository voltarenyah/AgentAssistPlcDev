# ADR-0004 Revert the notion-kit UI migration

## Status

Accepted

## Context

`@notion-kit/ui` was added as a Studio dependency and then migrated across the
product surfaces over 89 commits, with `ADR-0004-notion-kit-design-token-authority`
and `ADR-0005-primitive-mapping-for-missing-notion-kit-counterparts` recording the
design, alongside `docs/ui-spec/notion-kit-ui-migration-ui-spec.md`. Both of those
ADRs and that spec are removed by the same revert that adds this one.

The migration's justification was circular. ADR-0004 existed to resolve a token-name
collision in which notion-kit's `:root` declarations overrode Studio's surface tokens
and made text invisible (1.31:1 light, 1.05:1 dark). That collision existed *only*
because the library was rendering product UI. With the library out of the product
there is no collision, so there is no defect for that ADR to address.

The migration also replaced Studio's design language rather than extending it. Measured
against the pre-migration values:

| | Studio | notion-kit |
|---|---|---|
| Filled action button | black (light) / white (dark) | `#2383e2` blue |
| Button radius | 10px | 4px |
| Button and field label | 10-11px | 14px |
| Card radius | 14px | 12px |
| Dark border | white 7% | white 10% |

Because the library's `primary` variant is an *outline*, mapping Studio's filled
`default` button onto it left only `blue`, so 26 prominent actions changed color
identity. Roughly 90% of surfaces were converted, which left two control languages
coexisting: one screen carried twelve distinct button geometries, and holding the
original sizes required 60+ hand-fitted `!` overrides.

The visible symptom was dialog width, and correcting the diagnosis here because the first
version of it was wrong. notion-kit's `DialogContent` carries a breakpoint-prefixed
`md:max-w-[calc(100%-2rem)]`. Studio's own ends with a breakpoint-prefixed
`sm:max-w-lg` (512px). `tailwind-merge` cannot dedupe a variant-prefixed class against an
unprefixed caller value, so **both** libraries silently discarded every caller's
`max-w-*`. Six dialogs author a width — 560, 560, 620, 760 and 896px — and all of them
rendered at 512px.

What the migration changed was the failure *mode*, not the existence of the bug:

| | caller width | rendered |
|---|---|---|
| Studio's own `sm:max-w-lg` | 620px | **512px** — wrong, but benign |
| notion-kit's `md:max-w-[calc(100%-2rem)]` | 620px | **1568px** — edge to edge |

Measured on the create-workbench dialog at a 1600px viewport. So the revert removed the
catastrophic form of the defect, but the pre-existing form survived it and was then fixed
at the primitive: `DialogContent` now uses a single unprefixed
`max-w-[min(32rem,calc(100%-2rem))]`, which is pixel-identical for the default case and
sits in the same tailwind-merge group as a caller's `max-w-[...]`, so the caller's value
now dedupes and wins. The five callers whose widths had been dead code carry their own
`min(<width>,calc(100%-2rem))` so the small-screen gutter is preserved.

This is not detectable by the build or the suite, because happy-dom applies no Tailwind
CSS, and it was not caught by browser sweeps that asserted content and console
cleanliness rather than comparing rendered geometry to intent.

## Decision Point

- **Question**: Should Studio's product surfaces keep using `@notion-kit/ui`, or return
  to Studio's own primitives and tokens?
- **Why a decision exists**: The library is a coherent, maintained system and gives a
  consistent control vocabulary, at the cost of overriding this application's
  deliberate visual identity. Keeping it means continuing to hand-fit `!` overrides
  against library defaults and finishing the remaining ~10% of surfaces. Reverting
  discards a day of work but restores the identity and removes the collision class of
  defect entirely.
- **Scope boundary**: Studio product UI under `studio/src/studio/`. This ADR does not
  govern unrelated dependencies, and it does not rule out a future, deliberate design
  system adoption (see Reconsider when).

## Decision

Studio product surfaces use the primitives in `studio/src/components/ui/` and the
tokens in `studio/src/assets/main.css`. `@notion-kit/ui` is not a product dependency,
and Studio does not inherit a third-party design system's geometry, palette, or control
typography by default.

### Decision Details

| Item | Content |
|---|---|
| **Decision** | Revert all 85 migration-touched paths to their pre-library content; re-apply only the two commits in that range that are independent of the library; fix the shared `DialogContent` width so caller widths are honoured. |
| **Why this** | The migration's core justification was circular, and its net effect on the product was negative and measurable. |
| **Known unknowns** | Whether a Notion-like component showcase is wanted for its own sake; the reverted `SandboxDeniedDialog` loses the tests it gained during the migration. |
| **Reconsider when** | Adopting a design system is a deliberate, signed-off design decision with a visual target to verify against, rather than inheriting a library's defaults surface by surface. |

### Verification rules this produced

- **A Tailwind collision is invisible to the build and the suite.** `happy-dom` applies
  no CSS, so a class that silently loses to a library utility type-checks and passes
  every test. Any `className` that overrides a utility a component also sets must be
  confirmed in a real browser by reading computed style, not by a green run.
- **Prefer an unprefixed utility when overriding.** A caller override cannot beat a
  *variant-prefixed* library utility without `!`, because `tailwind-merge` only dedupes
  within the same variant. Check the component's own classes before assuming an override
  applies.
- **Compare rendered geometry to intent, not just to "no errors".** The regression
  reached the product because sweeps asserted that content was present, the console was
  clean, and there was no horizontal overflow. Asserting a dialog's actual width against
  its intended width would have caught it immediately.
