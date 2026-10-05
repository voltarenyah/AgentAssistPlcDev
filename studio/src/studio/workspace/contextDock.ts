// Pure derivation of the right context dock: what (if anything) the dock shows
// for the current selection and focused workspace view. Focus follows the
// selected tab of the ACTIVE FlexLayout tabset (WorkspaceService), so after
// splits this mapping just re-runs with the new focused kind. When landing or
// hardware pages are shown the workspace is not visible and focusedView may be
// stale — the rules below keep the device dock out of those states.
//
// Version control is a worktree-level concept: on the worktree landing page
// (no device selected) the right dock hosts the version control panel.
//
// A device on a chat or source view has no dock at all: the AI sessions page
// that used to fill it is gone, and the conversations it listed live in the
// navigator's SESSIONS section.

import type { WorkspaceViewKind } from './workspaceTypes'

export type ContextDockContent =
  | { kind: 'none' }
  | { kind: 'hardware' }
  | { kind: 'device' }
  | { kind: 'knowledge' }
  | { kind: 'version-control' }

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
  if (deviceId !== null && focusedView === 'knowledge') {
    return hasKnowledgeContext
      ? { visible, content: { kind: 'knowledge' } }
      : { visible, content: none }
  }
  // A device on a chat, source, inspector or stale focus resolves to no dock at all, so neither the
  // dock shell nor its resize handle renders: an empty one would reserve room for a list that is not
  // there, because the AI sessions page that used to fill this state is gone.
  if (deviceId !== null) return { visible: false, content: none }
  return { visible, content: none }
}
