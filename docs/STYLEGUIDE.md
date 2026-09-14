# Automation Workbench UI Style Guide

This is the visual and component-usage contract for Studio. It keeps the
workbench dense, quiet, and predictable while leaving visual emphasis for PLC,
version-control, and runtime state. It is intentionally based on the Orca UI
library from which Studio was originally imported.

## Source of truth

| Concern | Canonical location |
| --- | --- |
| Color, radius, typography, and shared chrome tokens | `studio/src/assets/main.css` |
| UI primitives | `studio/src/components/ui/` |
| Domain-specific compositions | `studio/src/studio/` |
| Class-name merging | `studio/src/lib/utils.ts` (`cn`) |

Use the semantic tokens from `main.css`; do not add literal colors, arbitrary
font sizes, radius values, or shadow tiers in component code when the existing
tokens and Tailwind scale cover the role. A new global token must work in both
light and dark themes and have a named, reusable role.

## Component library

Studio's component library is the Shadcn-style, Radix-backed set in
`studio/src/components/ui/`. These are the supported building blocks:

- Actions and fields: `Button`, `ButtonGroup`, `Input`, `Checkbox`, `Label`,
  `Select`, `Slider`, `Switch`, `Toggle`, `ToggleGroup`, `ColorPicker`.
- Content and status: `Card`, `Badge`, `Progress`, `Separator`, `ScrollArea`,
  `Tabs`, `Accordion`, `Collapsible`.
- Transient and layered UI: `Tooltip`, `Popover`, `HoverCard`, `Dialog`,
  `Sheet`, `DropdownMenu`, `ContextMenu`, `Command`, and Sonner toasts.

Use the primitive before creating a local replacement. Preserve its
`data-slot`, keyboard behavior, focus handling, and existing variants. Use
`cn()` with caller `className` last when extending a primitive.

### Selection rules

| Need | Use | Do not replace it with |
| --- | --- | --- |
| One affirmative action | `Button` default | a custom filled button |
| Lower-emphasis or toolbar action | `Button` secondary, outline, or ghost | a new button class |
| Icon-only control label | `Tooltip` with an existing button | a native `title` or bespoke hover element |
| Click menu of actions | `DropdownMenu` | a hand-built popover menu |
| Contextual right-click actions | `ContextMenu` | a dropdown menu |
| Arbitrary, non-blocking picker/form | `Popover` | a custom positioned surface |
| Blocking decision | `Dialog` | an inline overlay or popover |
| Edge drawer | `Sheet` | a dialog styled as a drawer |
| Known single choice | `Select` | a custom listbox |
| Searchable choice | `Command` in a `Popover` | a custom combobox |
| Brief completion feedback | Sonner toast | a dialog or permanent message |
| Persistent status | inline text plus `Badge` | a toast |

## Visual rules

- Use `background` for the canvas, `card` for raised panels, `popover` for
  floating surfaces, `muted` for secondary content, and `accent` for hover or
  active list rows. Pair every surface token with its foreground token.
- Use `primary` only for the single affirmative action in a flow;
  `destructive` only for irreversible actions or errors.
- Use `var(--font-mono)` for literal identifiers, paths, hashes, PLC source,
  and technical metrics. Keep normal application text in the configured Geist
  family.
- Use the primitive's established size and radius. For dense Studio chrome,
  prefer existing `xs`, `sm`, `icon-xs`, and `icon-sm` button variants rather
  than adding a new compact control.
- Use Lucide icons only. Icons inherit surrounding text color and should have a
  visible tooltip when their meaning is not self-evident.
- Keep shadows sparse: border for ordinary separation, existing primitive
  shadow for subtle lift, and the existing overlay treatment for floating UI.

## Adding or composing UI

Create a domain component under `studio/src/studio/` only when it represents a
specific workbench concept or owns domain behavior (for example a tag picker or
version-control comparison). Compose existing primitives inside it.

Add a new primitive under `components/ui/` only when all of the following are
true:

1. No existing primitive has the required semantics.
2. The behavior is expected to serve at least two independently maintained
   Studio surfaces.
3. It has a clear accessible interaction contract and focused tests.
4. Its variants and tokens are documented here when they expand the library.

Otherwise keep the implementation local and minimal; do not promote a
one-off layout, card, chip, button, menu, dialog, or input wrapper into the
library. Do not copy a primitive merely to tweak colors or spacing—extend it
through supported variants or `className`.

## Resolution order

When the guide is silent, resolve a UI choice in this order:

1. The task's approved UI requirements and accessibility needs.
2. A compatible Studio primitive.
3. A representative Studio domain component.
4. The corresponding primitive in the Orca source repository.
5. A small local composition, then a new primitive only when it meets the
   addition criteria above.
