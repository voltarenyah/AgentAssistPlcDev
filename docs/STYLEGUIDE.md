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

The component registry URL documented by Notion UI returned 404 on 2026-09-21.
Use the installed package exports until that registry is restored.
