import { Code2, Cpu, Database, GitBranch, MessageSquare, type LucideIcon } from 'lucide-react'
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
import ChatWorkspace from '@/studio/chat/ChatWorkspace'
import PlcSourcePanel from '@/studio/PlcSourcePanel'
import NodeEdgesView from '@/studio/NodeEdgesView'
import VersionControlPanel from '@/studio/version-control/VersionControlPanel'
import DeviceOverviewView, { type DeviceOverviewViewProps } from '@/studio/DeviceOverviewView'

export type StudioTab = 'overview' | 'chat' | 'source' | 'knowledge' | 'git'

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
}

export type WorkspaceHostProps = {
  activeTab: StudioTab
  onTabChange: (tab: StudioTab) => void
  overview: DeviceOverviewViewProps
  chat: WorkspaceChatProps
  source: WorkspaceSourceProps
  knowledge: WorkspaceKnowledgeProps
  git: WorkspaceGitProps
}

const tabs: Array<{ id: StudioTab; label: string; icon: LucideIcon }> = [
  { id: 'overview', label: 'Device overview', icon: Cpu },
  { id: 'chat', label: 'AI chat', icon: MessageSquare },
  { id: 'source', label: 'PLC source', icon: Code2 },
  { id: 'knowledge', label: 'Knowledge', icon: Database },
  { id: 'git', label: 'Version control', icon: GitBranch },
]

export default function WorkspaceHost({ activeTab, onTabChange, overview, chat, source, knowledge, git }: WorkspaceHostProps) {
  return (
    <>
      <div className="flex h-10 shrink-0 items-center gap-1 border-b px-3" style={{ borderColor: 'var(--border)' }}>
        {tabs.map(tab => {
          const Icon = tab.icon
          return (
            <button
              key={tab.id}
              onClick={() => onTabChange(tab.id)}
              className={`flex h-7 items-center gap-1.5 rounded-md px-2.5 text-[9px] transition-colors ${activeTab === tab.id ? 'bg-accent text-foreground' : 'text-muted-foreground hover:bg-accent/50 hover:text-foreground'}`}
            >
              <Icon className="h-3 w-3" /> {tab.label}
            </button>
          )
        })}
        <div className="flex-1" />
      </div>

      <div className="scrollbar-sleek min-h-0 flex-1 overflow-y-auto">
        {activeTab === 'overview' && (
          <DeviceOverviewView {...overview} />
        )}

        {activeTab === 'chat' && (
          <div className="h-full min-h-[520px]">
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
        )}

        {activeTab === 'source' && source.workbenchId && source.worktreeId && source.deviceId && (
          <PlcSourcePanel
            workbenchId={source.workbenchId}
            worktreeId={source.worktreeId}
            deviceId={source.deviceId}
            deviceView={source.deviceView}
            onChatWithAgent={source.onChatWithAgent}
            onSnapshotReload={source.onSnapshotReload}
          />
        )}

        {activeTab === 'knowledge' && (
          <div className="flex h-full min-h-[560px] flex-col p-5">
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
        )}

        {activeTab === 'git' && (
          <div className="h-full min-h-[520px]">
            <VersionControlPanel
              workbenchId={git.workbenchId}
              worktreeId={git.worktreeId}
              onSelectionChange={git.onSelectionChange}
            />
          </div>
        )}
      </div>
    </>
  )
}
