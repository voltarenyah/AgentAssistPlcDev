# External Resources

## Overview

This file records project-level access methods for resources outside the
repository. Feature documents reference these labels and add feature-specific
identifiers.

## Frontend

| Label | Axis | Status | Access Method | Owner / Notes |
| --- | --- | --- | --- | --- |
| frontend-notion-design-direction | Guidelines | present | https://github.com/VoltAgent/awesome-design-md/blob/main/design-md/notion/DESIGN.md | Canonical UI/UX guide for Studio. It replaces the former local visual and component rules. |
| frontend-notion-ui-components | Design System | present | `studio/package.json`: `@notion-kit/ui`; import from `@notion-kit/ui/primitives` | Installed Notion UI components; styles load from `studio/src/assets/main.css`. |
| frontend-visual-verification | Visual Verification Environment | present | `./launch.ps1`, then http://localhost:5173/ | Inspect changes in the running Studio UI. |

## Update History

| Date | Change | Author |
| --- | --- | --- |
| 2026-09-21 | Replaced the former local Studio design rules with the Notion design analysis. | Codex |
| 2026-09-21 | Installed `@notion-kit/ui` and recorded its Studio import path. | Codex |
