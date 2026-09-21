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

Wrap any surface that renders these primitives in `.notion-kit-surface`
(`studio/src/assets/main.css`). The library resolves its colors through
`:root` variables that Studio reuses for surface colors, so without the wrapper
its `text-muted` and `text-secondary` text is painted in Studio's surface color
and is unreadable in both themes.

`Button` is the one primitive that does not default its `size`, so always pass
one: `sm` is `h-8 px-3` and `md` is `h-9 px-4 py-2`, which matches Studio's own
default button height. A `Button` given only a `variant` renders with no height
and no padding, leaving the label against the border.

The component registry URL documented by Notion UI returned 404 on 2026-09-21.
Use the installed package exports until that registry is restored.
