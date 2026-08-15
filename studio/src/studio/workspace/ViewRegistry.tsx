// The single place that knows which component renders a workspace view kind.
// WorkbenchViewHost resolves instances through this registry; the layout model
// and its tabs only carry instanceId/kind.

import type { ReactNode } from 'react'
import { Code2, Cpu, Database, GitBranch, MessageSquare, type LucideIcon } from 'lucide-react'
import ChatWorkspace from '@/studio/chat/ChatWorkspace'
import PlcSourcePanel from '@/studio/PlcSourcePanel'
import NodeEdgesView from '@/studio/NodeEdgesView'
import VersionControlPanel from '@/studio/version-control/VersionControlPanel'
import DeviceOverviewView from '@/studio/DeviceOverviewView'
import type { WorkspaceViewKind, WorkspaceViewProps } from './workspaceTypes'

export type WorkspaceViewDefinition = {
  title: string
  icon: LucideIcon
  render: (props: WorkspaceViewProps) => ReactNode
}

export const workspaceViewRegistry: Record<WorkspaceViewKind, WorkspaceViewDefinition> = {
  overview: {
    title: 'Device overview',
    icon: Cpu,
    render: ({ overview }) => (
      <div className="scrollbar-sleek h-full overflow-y-auto">
        <DeviceOverviewView {...overview} />
      </div>
    ),
  },
  chat: {
    title: 'AI chat',
    icon: MessageSquare,
    render: ({ chat }) => (
      <div className="h-full min-h-0">
        <ChatWorkspace
          tabs={chat.tabs}
          busy={chat.busy}
          onCreateSession={chat.onCreateSession}
          confirmation={chat.confirmation}
          onConfirm={chat.onConfirm}
          onFocus={chat.onFocus}
          onSend={chat.onSend}
          onDraftChange={chat.onDraftChange}
          onStop={chat.onStop}
          onContinue={chat.onContinue}
          sourceContext={chat.sourceContext}
          onClearSourceContext={chat.onClearSourceContext}
        />
      </div>
    ),
  },
  source: {
    title: 'PLC source',
    icon: Code2,
    render: ({ source }) => (
      <div className="h-full min-h-0">
        {source.workbenchId && source.worktreeId && source.deviceId && (
          <PlcSourcePanel
            workbenchId={source.workbenchId}
            worktreeId={source.worktreeId}
            deviceId={source.deviceId}
            deviceView={source.deviceView}
            onChatWithAgent={source.onChatWithAgent}
            onSnapshotReload={source.onSnapshotReload}
          />
        )}
      </div>
    ),
  },
  knowledge: {
    title: 'Knowledge',
    icon: Database,
    render: ({ knowledge }) => (
      <div className="flex h-full min-h-0 flex-col p-5">
        <div className="flex min-h-0 flex-1 flex-col overflow-hidden rounded-xl border bg-card" style={{ borderColor: 'var(--border)' }}>
          {knowledge.context && (
            <NodeEdgesView
              context={knowledge.context}
              projectName={knowledge.projectName}
              onNodeSelect={knowledge.onNodeSelect}
              onEdgeSelect={knowledge.onEdgeSelect}
            />
          )}
        </div>
      </div>
    ),
  },
  git: {
    title: 'Version control',
    icon: GitBranch,
    render: ({ git }) => (
      <div className="h-full min-h-0">
        <VersionControlPanel
          workbenchId={git.workbenchId}
          worktreeId={git.worktreeId}
          onSelectionChange={git.onSelectionChange}
        />
      </div>
    ),
  },
}
