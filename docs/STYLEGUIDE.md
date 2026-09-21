# Automation Workbench UI/UX Guide

The canonical design guide for every Studio UI/UX change is
[Notion design analysis](https://github.com/VoltAgent/awesome-design-md/blob/main/design-md/notion/DESIGN.md).
Use the current version of that document when designing, implementing, or
reviewing UI. It replaces the former local Studio visual and component rules.

The URL is intentionally the source rather than a copied snapshot, so updates
to the guide are available to future work. Its project record and access method
are in `docs/project-context/external-resources.md`.

Product requirements, safety constraints, and accessibility requirements still
apply where the design guide does not address them.

## Reusable Notion UI components

`@notion-kit/ui` is installed in `studio/`, and its global stylesheet is loaded
from `studio/src/assets/main.css`. Import a primitive directly when it fits the
current UI:

```tsx
import { Button } from "@notion-kit/ui/primitives";
```

`@notion-kit/ui` owns the token names both systems declare — `--primary`,
`--secondary`, `--muted`, `--border` and `--radius`. Studio's own surfaces use
`--surface-muted` and `--surface-secondary` instead of overloading those names,
so the collision that once made notion-kit text invisible cannot recur. Import
the primitives directly; no wrapper scope is needed. The decision and the two
deliberate accessibility deviations are in
`docs/adr/ADR-0004-notion-kit-design-token-authority.md`.

`Button` is the one primitive that does not default its `size`, so always pass
one: `sm` is `h-8 px-3` and `md` is `h-9 px-4 py-2`, which matches Studio's own
default button height. A `Button` given only a `variant` renders with no height
and no padding, leaving the label against the border.

The component inventory and per-component install instructions are at
<https://notion-ui.vercel.app/docs>; `studio/AGENTS.md` records how to use that
catalogue. Use the installed package exports for anything already a dependency.
