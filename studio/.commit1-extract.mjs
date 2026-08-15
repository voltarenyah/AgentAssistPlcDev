// One-shot Commit 1 extraction script. Line-based; preserves per-line endings of untouched lines.
import { readFileSync, writeFileSync } from 'node:fs'

const mainPath = 'src/studio/MainStudio.tsx'
const original = readFileSync(mainPath, 'utf8')
const lines = original.split('\n') // entries may carry a trailing '\r'
const strip = line => (line.endsWith('\r') ? line.slice(0, -1) : line)

if (lines.length !== 2497 || lines[2496] !== '') throw new Error(`expected 2496 lines + trailing newline, got ${lines.length}`)

const assertLine = (n, expected) => {
  const actual = strip(lines[n - 1])
  if (actual !== expected) throw new Error(`line ${n}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`)
}

// ---------- verify boundaries (1-based line numbers from Read) ----------
assertLine(334, 'function Metric({')
assertLine(353, '}')
assertLine(354, '')
assertLine(355, 'function CompileApprovalDialog({')
assertLine(1762, '  const tabs: Array<{ id: StudioTab; label: string; icon: typeof Boxes }> = [')
assertLine(1768, '  ]')
assertLine(1769, '')
assertLine(98, "type StudioTab = 'overview' | 'chat' | 'source' | 'knowledge' | 'git'")
assertLine(2028, '          ) : (')
assertLine(2029, '            <>')
assertLine(2048, '                  <div className="mx-auto max-w-6xl space-y-5 p-5">')
assertLine(2196, '                  </div>')
assertLine(2197, '                )}')
assertLine(2265, '              </div>')
assertLine(2266, '            </>')
assertLine(2267, '          )}')

// ---------- extract Metric (lines 334-353) ----------
const metric = lines.slice(333, 353).map(strip).join('\n')

// ---------- extract + transform overview JSX (lines 2048-2196) ----------
let overviewJsx = lines.slice(2047, 2196).map(strip).join('\n')
const substitutions = [
  ['selection.deviceId', 'deviceId'],
  ['void openProjectInTia()', 'onOpenProjectInTia()'],
  ['void attachTiaInstance(matchingTiaSession.id)', 'onAttachTiaInstance(matchingTiaSession.id)'],
  ['void stageRefresh()', 'onStageRefresh()'],
  ['void rebuildProject()', 'onRebuildProject()'],
  ['void updateKnowledge(false)', 'onUpdateKnowledge(false)'],
  ['void updateKnowledge(true)', 'onUpdateKnowledge(true)'],
  ['void mergeIntoMaster()', 'onMergeIntoMaster()'],
  ['void bootstrapDevice()', 'onBootstrapDevice()'],
]
for (const [from, to] of substitutions) {
  if (!overviewJsx.includes(from)) throw new Error(`overview substitution source missing: ${from}`)
  overviewJsx = overviewJsx.split(from).join(to)
}
if (overviewJsx.includes('selection.') || /void (openProjectInTia|attachTiaInstance|stageRefresh|rebuildProject|updateKnowledge|mergeIntoMaster|bootstrapDevice)/.test(overviewJsx)) {
  throw new Error('overview JSX still references MainStudio internals')
}
overviewJsx = overviewJsx.split('\n').map(line => {
  if (line.trim() === '') return line
  if (!line.startsWith('              ')) throw new Error(`cannot dedent line: ${JSON.stringify(line)}`)
  return line.slice(14)
}).join('\n')

// ---------- DeviceOverviewView.tsx ----------
const deviceOverviewView = `import {
  AlertCircle,
  ArrowDownToLine,
  CircleDot,
  Cpu,
  Database,
  GitMerge,
  RefreshCw,
  RotateCw,
  Server,
  ShieldCheck,
  Sparkles,
} from 'lucide-react'
import type {
  ChatSessionInfo,
  DeviceExportMetadata,
  DeviceInfo,
  KnowledgeVisualState,
  OfflineBlockInfo,
  SessionInfo,
  WorkbenchRegistration,
} from '@/api/client'
import type { DeviceViewState } from '@/studio/deviceSnapshot'

export type DeviceOverviewViewProps = {
  deviceName: string | null
  deviceId: string | null
  deviceInfo: DeviceInfo | null
  deviceMeta: DeviceExportMetadata | null
  deviceView: DeviceViewState | null
  blocks: OfflineBlockInfo[]
  displayedSourceObjectCount: number
  deviceSessions: ChatSessionInfo[]
  activeKnowledge: KnowledgeVisualState
  isBrandNewDevice: boolean
  matchingTiaSession: SessionInfo | null
  operation: string | null
  rebuildArmed: boolean
  setRebuildArmed: (armed: boolean) => void
  activeWorktree: WorkbenchRegistration | null
  onOpenProjectInTia: () => void
  onAttachTiaInstance: (sessionId: number) => void
  onStageRefresh: () => void
  onRebuildProject: () => void
  onUpdateKnowledge: (rebuild: boolean) => void
  onMergeIntoMaster: () => void
  onBootstrapDevice: () => void
}

${metric}

export default function DeviceOverviewView({
  deviceName,
  deviceId,
  deviceInfo,
  deviceMeta,
  deviceView,
  blocks,
  displayedSourceObjectCount,
  deviceSessions,
  activeKnowledge,
  isBrandNewDevice,
  matchingTiaSession,
  operation,
  rebuildArmed,
  setRebuildArmed,
  activeWorktree,
  onOpenProjectInTia,
  onAttachTiaInstance,
  onStageRefresh,
  onRebuildProject,
  onUpdateKnowledge,
  onMergeIntoMaster,
  onBootstrapDevice,
}: DeviceOverviewViewProps) {
  return (
${overviewJsx}  )
}
`

// ---------- WorkspaceHost.tsx ----------
const workspaceHost = `import { Code2, Cpu, Database, GitBranch, MessageSquare, type LucideIcon } from 'lucide-react'
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
              className={\`flex h-7 items-center gap-1.5 rounded-md px-2.5 text-[9px] transition-colors \${activeTab === tab.id ? 'bg-accent text-foreground' : 'text-muted-foreground hover:bg-accent/50 hover:text-foreground'}\`}
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
`

// ---------- rebuild MainStudio.tsx line-wise (bottom-up) ----------
const out = [...lines]

// device branch fragment: lines 2029-2266 -> <WorkspaceHost .../> (use CRLF to match replaced region)
const hostJsx = `            <WorkspaceHost
              activeTab={activeTab}
              onTabChange={setActiveTab}
              overview={{
                deviceName,
                deviceId: selection.deviceId,
                deviceInfo,
                deviceMeta,
                deviceView,
                blocks,
                displayedSourceObjectCount,
                deviceSessions,
                activeKnowledge,
                isBrandNewDevice,
                matchingTiaSession,
                operation,
                rebuildArmed,
                setRebuildArmed,
                activeWorktree,
                onOpenProjectInTia: () => void openProjectInTia(),
                onAttachTiaInstance: sessionId => void attachTiaInstance(sessionId),
                onStageRefresh: () => void stageRefresh(),
                onRebuildProject: () => void rebuildProject(),
                onUpdateKnowledge: rebuild => void updateKnowledge(rebuild),
                onMergeIntoMaster: () => void mergeIntoMaster(),
                onBootstrapDevice: () => void bootstrapDevice(),
              }}
              chat={{
                tabs: chatTabs,
                busy: chatBusy,
                confirmation: pendingConfirmation,
                onConfirm: decision => void decideConfirmation(decision),
                onFocus: sessionId => void activateChatSession(sessionId),
                onSend: (sessionId, message) => void sendChatMessage(sessionId, message),
                onDraftChange: (sessionId, draft) => setChatTabs(previous => setDraft(previous, sessionId, draft)),
                onStop: stopChatGeneration,
                onContinue: sessionId => void continueChat(sessionId),
                sourceContext: chatSourceContext,
                onClearSourceContext: () => setChatSourceContext(null),
              }}
              source={{
                workbenchId: selection.workbenchId,
                worktreeId: selection.worktreeId,
                deviceId: selection.deviceId,
                deviceView,
                onChatWithAgent: item => {
                  setChatSourceContext({
                    name: item.name,
                    category: item.category,
                    number: item.number,
                    relativePath: item.relativePath,
                    plcName: deviceName ?? '',
                  })
                  setActiveTab('chat')
                },
                onSnapshotReload: () => void reloadDeviceSnapshot({
                  workbenchId: selection.workbenchId!,
                  worktreeId: selection.worktreeId!,
                  deviceId: selection.deviceId!,
                }),
              }}
              knowledge={{
                context: knowledgeContext,
                projectName: deviceName ?? '',
                onNodeSelect: node => setKnowledgeSelection(previous => ({ ...previous, node })),
                onEdgeSelect: edge => setKnowledgeSelection(previous => ({ ...previous, edge })),
              }}
              git={{
                workbenchId: selection.workbenchId!,
                worktreeId: selection.worktreeId!,
                onSelectionChange: setVersionControlSelection,
              }}
            />`.split('\n')

const fragmentHasCR = lines[2028].endsWith('\r') // line 2029 '<>'
const hostLines = fragmentHasCR ? hostJsx.map(line => line + '\r') : hostJsx
out.splice(2028, 2266 - 2029 + 1, ...hostLines)

// tabs array: lines 1762-1769 (incl. trailing blank)
out.splice(1761, 1769 - 1762 + 1 + 1)

// Metric: lines 334-354 (incl. trailing blank; one blank remains before CompileApprovalDialog)
out.splice(333, 354 - 334 + 1 + 1)

// StudioTab type: line 98
out.splice(97, 1)

// imports: remove by exact content match (unique lines)
const removeByContent = content => {
  const indexes = out.reduce((acc, line, index) => (strip(line) === content ? [...acc, index] : acc), [])
  if (indexes.length !== 1) throw new Error(`import line not unique/found: ${content} (${indexes.length})`)
  out.splice(indexes[0], 1)
}
removeByContent("import VersionControlPanel from '@/studio/version-control/VersionControlPanel'")
removeByContent("import ChatWorkspace from '@/studio/chat/ChatWorkspace'")
removeByContent("import NodeEdgesView from '@/studio/NodeEdgesView'")
removeByContent("import PlcSourcePanel from '@/studio/PlcSourcePanel'")
for (const icon of ['  ArrowDownToLine,', '  Code2,', '  Database,', '  GitMerge,', '  MessageSquare,', '  RotateCw,']) {
  removeByContent(icon)
}

// add WorkspaceHost import after the plcSourceState type import, matching its line ending
const anchor = "import type { SourceChatContext } from '@/studio/plcSourceState'"
const anchorIndexes = out.reduce((acc, line, index) => (strip(line) === anchor ? [...acc, index] : acc), [])
if (anchorIndexes.length !== 1) throw new Error('plcSourceState import anchor not unique')
const anchorLine = out[anchorIndexes[0]]
const hostImport = "import WorkspaceHost, { type StudioTab } from '@/studio/workspace/WorkspaceHost'" + (anchorLine.endsWith('\r') ? '\r' : '')
out.splice(anchorIndexes[0] + 1, 0, hostImport)

const next = out.join('\n')

// ---------- post-conditions ----------
for (const gone of ['function Metric({', 'ArrowDownToLine', 'Code2', 'GitMerge', 'MessageSquare', 'RotateCw',
  'ChatWorkspace', 'PlcSourcePanel', 'NodeEdgesView', 'VersionControlPanel']) {
  if (next.includes(gone)) throw new Error(`MainStudio still references ${gone}`)
}
if (next.includes('{tabs.map') || next.includes('const tabs:')) throw new Error('MainStudio still references the moved `tabs` array')
if (/[^A-Za-z]Database[^A-Za-z]/.test(next)) throw new Error('MainStudio still references Database icon')
const countOccurrences = needle => next.split(needle).length - 1
if (countOccurrences('StudioTab') !== 2) throw new Error(`StudioTab occurrences: ${countOccurrences('StudioTab')}`)
if (countOccurrences('<WorkspaceHost') !== 1) throw new Error('WorkspaceHost usage count wrong')
if (!next.includes('          ) : (\n            <WorkspaceHost') && !next.includes('          ) : (\r\n            <WorkspaceHost')) {
  throw new Error('WorkspaceHost not spliced into the device branch')
}

writeFileSync('src/studio/DeviceOverviewView.tsx', deviceOverviewView)
writeFileSync('src/studio/workspace/WorkspaceHost.tsx', workspaceHost)
writeFileSync(mainPath, next)
console.log('OK: DeviceOverviewView.tsx, workspace/WorkspaceHost.tsx written; MainStudio.tsx rewritten')
