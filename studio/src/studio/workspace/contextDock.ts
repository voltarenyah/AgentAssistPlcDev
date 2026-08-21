// Pure derivation of the right context dock: what (if anything) the dock shows
// for the current selection and focused workspace view. Focus follows the
// selected tab of the ACTIVE FlexLayout tabset (WorkspaceService), so after
// splits this mapping just re-runs with the new focused kind. When landing or
// hardware pages are shown the workspace is not visible and focusedView may be
// stale — the rules below keep device/session docks out of those states.
//
// Version control is a worktree-level concept: on the worktree landing page
// (no device selected) the right dock hosts the version control panel.

import type { WorkspaceViewKind } from './workspaceTypes'

export type ContextDockContent =
  | { kind: 'none' }
  | { kind: 'hardware' }
  | { kind: 'device' }
  | { kind: 'knowledge' }
  | { kind: 'version-control' }
  | { kind: 'sessions' }

export type ContextDockState = {
  /** Whether the dock shell (resize handle + panel) renders at all. */
  visible: boolean
  content: ContextDockContent
}

export type ContextDockInputs = {
  worktreeId: string | null
  deviceId: string | null
  mainViewKind: 'project' | 'worktree' | 'hardware' | 'device'
  hardwarePage: 'tree' | 'bom' | 'network' | null
  focusedView: WorkspaceViewKind | null
  hasKnowledgeContext: boolean
}

const none: ContextDockContent = { kind: 'none' }

export const resolveContextDock = (inputs: ContextDockInputs): ContextDockState => {
  const { worktreeId, deviceId, mainViewKind, hardwarePage, focusedView, hasKnowledgeContext } = inputs

  const visible = Boolean(worktreeId)
    && (deviceId !== null || mainViewKind === 'hardware' || mainViewKind === 'worktree')
  if (!visible) return { visible: false, content: none }

  // Worktree landing page: the version control panel is the worktree-level dock.
  if (deviceId === null && mainViewKind === 'worktree') {
    return { visible, content: { kind: 'version-control' } }
  }
  // Hardware page without a device: the properties dock wins.
  if (deviceId === null && mainViewKind === 'hardware' && hardwarePage === 'tree') {
    return { visible, content: { kind: 'hardware' } }
  }
  if (deviceId !== null && focusedView === 'overview') {
    return { visible, content: { kind: 'device' } }
  }
  if (deviceId !== null && focusedView === 'knowledge' && hasKnowledgeContext) {
    return { visible, content: { kind: 'knowledge' } }
  }
  // Sessions fallback covers chat/source and a (normally impossible) null focus.
  if (deviceId !== null && focusedView !== 'overview' && focusedView !== 'knowledge') {
    return { visible, content: { kind: 'sessions' } }
  }
  return { visible, content: none }
}
