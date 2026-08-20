// Workspace architecture types for the device workspace.
// FlexLayout owns geometry only (tabsets, splits, sizes) — these types describe
// WHAT a view is, never where it sits or how big it is. Domain data (chat tabs,
// device snapshots, …) lives in MainStudio and flows down as props bundles.

import type {
  GraphEdge,
  GraphNode,
  KnowledgeGraphContext,
  PendingConfirmation,
  SourceObjectInfo,
} from '@/api/client'
import type { ChatTabsState } from '@/studio/chat/chatTabState'
import type { SourceChatContext } from '@/studio/plcSourceState'
import type { DeviceViewState } from '@/studio/deviceSnapshot'
import type { DeviceOverviewViewProps } from '@/studio/DeviceOverviewView'

export type WorkspaceViewKind = 'overview' | 'chat' | 'source' | 'knowledge' | 'git'

/** One mounted view in the workspace. V1 uses exactly one instance per kind. */
export type WorkbenchViewInstance = {
  /** Stable per-instance id, also used as the FlexLayout tab id. */
  instanceId: string
  kind: WorkspaceViewKind
}

export const workspaceViewInstanceId = (kind: WorkspaceViewKind): string => `view:${kind}`

export const DEFAULT_WORKSPACE_VIEW_KINDS: readonly WorkspaceViewKind[] = [
  'overview',
  'chat',
  'source',
  'knowledge',
  'git',
]

export const defaultWorkspaceViewInstances = (): WorkbenchViewInstance[] =>
  DEFAULT_WORKSPACE_VIEW_KINDS.map(kind => ({ instanceId: workspaceViewInstanceId(kind), kind }))

export const workspaceViewKindForInstanceId = (instanceId: string): WorkspaceViewKind | null => {
  const kind = instanceId.replace(/^view:/, '')
  return (DEFAULT_WORKSPACE_VIEW_KINDS as readonly string[]).includes(kind)
    ? kind as WorkspaceViewKind
    : null
}

export type WorkspaceChatProps = {
  tabs: ChatTabsState
  busy: boolean
  onCreateSession?: () => void
  confirmation: PendingConfirmation | null
  onConfirm: (decision: 'allowOnce' | 'deny') => void
  onFocus: (sessionId: string) => void
  onSend: (sessionId: string, message: string) => void
  onDraftChange: (sessionId: string, draft: string) => void
  onStop: () => void
  onContinue: (sessionId: string) => void
  sourceContext: SourceChatContext | null
  onClearSourceContext: () => void
}

export type WorkspaceSourceProps = {
  workbenchId: string | null
  worktreeId: string | null
  deviceId: string | null
  deviceView: DeviceViewState | null
  onChatWithAgent: (item: SourceObjectInfo) => void
  onSnapshotReload: () => void
}

export type WorkspaceKnowledgeProps = {
  context: KnowledgeGraphContext | null
  projectName: string
  onNodeSelect: (node: GraphNode | null) => void
  onEdgeSelect: (edge: GraphEdge | null) => void
}

export type WorkspaceGitProps = {
  workbenchId: string
  worktreeId: string
  onSelectionChange: (selection: unknown) => void
  /** Starts a title-bar operation and returns its id, so long-running actions (full TIA compare) show live progress. */
  onBeginOperation?: (kind: string, label: string) => string
}

/** The props bundles every workspace view may draw from, keyed by view kind. */
export type WorkspaceViewProps = {
  overview: DeviceOverviewViewProps
  chat: WorkspaceChatProps
  source: WorkspaceSourceProps
  knowledge: WorkspaceKnowledgeProps
  git: WorkspaceGitProps
}
