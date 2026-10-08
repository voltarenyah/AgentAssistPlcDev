// Derivation of the right dock's content: which properties panel the `Properties`
// page shows for the current selection, and which page a session opens when the
// stored layout holds no page preference (ADR-0015).
//
// The dock itself no longer has a visibility matrix: the shell renders the rail on
// every surface, so no selection or focus combination resolves to "no dock". A
// selection that has no properties panel leaves the page on its named empty state.
//
// Focus follows the selected tab of the ACTIVE FlexLayout tabset (WorkspaceService),
// so after splits this mapping just re-runs with the new focused kind. When landing
// or hardware pages are shown the workspace is not visible and focusedView may be
// stale — the rules below keep a stale focus from choosing a panel for a selection
// that no longer exists.

import type { RightDockPage } from '../shellLayout'
import type { WorkspaceViewKind } from './workspaceTypes'

/** Which properties panel the `Properties` page renders for the current selection. */
export type PropertiesDockKind = 'hardware' | 'device' | 'knowledge'

export type ContextDockState = {
  /** The `Properties` page's panel, or null when the page shows its empty state. */
  properties: PropertiesDockKind | null
  /** The page a session opens before the user has chosen one. */
  defaultPage: RightDockPage
}

export type ContextDockInputs = {
  worktreeId: string | null
  deviceId: string | null
  mainViewKind: 'project' | 'worktree' | 'hardware' | 'device'
  hardwarePage: 'tree' | 'bom' | 'network' | null
  focusedView: WorkspaceViewKind | null
  hasKnowledgeContext: boolean
}

/** A selected device owns the page on every focus; knowledge wins only with a selection of its own. */
const resolveProperties = (inputs: ContextDockInputs): PropertiesDockKind | null => {
  const { worktreeId, deviceId, mainViewKind, hardwarePage, focusedView, hasKnowledgeContext } = inputs
  if (!worktreeId) return null
  if (deviceId !== null) {
    return focusedView === 'knowledge' && hasKnowledgeContext ? 'knowledge' : 'device'
  }
  // The hardware tree page shows the worktree's hardware target with no device selected. The BOM and
  // network pages clear the shell's hardware view, so their node selection has nothing to describe
  // and the page keeps its empty state.
  return mainViewKind === 'hardware' && hardwarePage === 'tree' ? 'hardware' : null
}

export const resolveContextDock = (inputs: ContextDockInputs): ContextDockState => {
  const properties = resolveProperties(inputs)
  // A selection with properties opens that page, which is what the equivalent surface shows today
  // (a device page shows device properties). A worktree with no device opens its working tree, which
  // is what the worktree landing page shows today. Nothing selected falls back to the page that
  // explains what to select.
  const defaultPage: RightDockPage = properties !== null
    ? 'properties'
    : inputs.worktreeId ? 'changes' : 'properties'
  return { properties, defaultPage }
}
