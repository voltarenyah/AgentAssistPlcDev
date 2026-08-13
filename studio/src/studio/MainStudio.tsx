import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  AlertCircle,
  ArrowDownToLine,
  Boxes,
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  CircleDot,
  CircuitBoard,
  ClipboardList,
  CloudCog,
  Code2,
  Cpu,
  Database,
  FileCode2,
  GitBranch,
  GitMerge,
  KeyRound,
  Loader2,
  MessageSquare,
  Network,
  PanelLeftClose,
  PanelLeftOpen,
  PanelRightClose,
  PanelRightOpen,
  Plus,
  RefreshCw,
  RotateCw,
  Search,
  Server,
  Settings2,
  ShieldCheck,
  Sparkles,
  Trash2,
  UploadCloud,
  Wrench,
  X,
} from 'lucide-react'
import { toast } from 'sonner'
import { ThemeToggle } from '@/catalog/ThemeToggle'
import { showErrorToast } from '@/components/ui/toast'
import VersionControlPanel from '@/studio/version-control/VersionControlPanel'
import VersionControlDetailsDock from '@/studio/version-control/VersionControlDetailsDock'
import WorkbenchNavigator, {
  type WorkbenchSelection,
} from '@/studio/workbench/WorkbenchNavigator'
import CreateWorkbenchDialog from '@/studio/workbench/CreateWorkbenchDialog'
import OperationStatusLine from '@/studio/workbench/OperationStatusLine'
import RuntimeStateStatusBar from '@/studio/workbench/RuntimeStateStatusBar'
import RefreshDialog from '@/studio/workbench/RefreshDialog'
import SandboxDeniedDialog from '@/studio/workbench/SandboxDeniedDialog'
import {
  applyDeviceSnapshot,
  beginDeviceSelection,
  completeDeviceSelection,
  failDeviceSelection,
  readDeviceMetadata,
  retainSnapshotOnError,
  rememberDeviceSnapshot,
  rememberDeviceSummary,
  type DeviceSelectionState,
} from '@/studio/deviceSnapshot'
import * as api from '@/api/client'
import ChatWorkspace from '@/studio/chat/ChatWorkspace'
import AppAssistantPanel from '@/studio/appAssistant/AppAssistantPanel'
import SessionDock from '@/studio/chat/SessionDock'
import NodeEdgesView from '@/studio/NodeEdgesView'
import KnowledgePropertiesDock from '@/studio/KnowledgePropertiesDock'
import DevicePropertiesDock from '@/studio/DevicePropertiesDock'
import HardwareConfigurationView from '@/studio/HardwareConfigurationView'
import HardwareBomView from '@/studio/HardwareBomView'
import HardwareNetworkView from '@/studio/HardwareNetworkView'
import HardwarePropertiesDock from '@/studio/HardwarePropertiesDock'
import ProjectLandingPage from '@/studio/workbench/ProjectLandingPage'
import WorktreeLandingPage from '@/studio/workbench/WorktreeLandingPage'
import McpToolsHelper from '@/studio/McpToolsHelper'
import {
  clampDockWidth,
  readShellLayout,
  writeShellLayout,
  type DockSide,
  type ShellLayout,
} from '@/studio/shellLayout'
import {
  appendAssistantDelta,
  appendLocalUserMessage,
  appendProgressMessage,
  closeTab,
  emptyChatTabs,
  openTab,
  renameTab,
  setDraft,
  setTurnMeta,
  type ChatTabsState,
} from '@/studio/chat/chatTabState'

type StudioTab = 'overview' | 'chat' | 'source' | 'knowledge' | 'git'
// What <main> renders for the current selection. Replaces the old hardwarePage
// ternary: project and worktree selections now have their own landing pages.
export type MainView =
  | { kind: 'project' }
  | { kind: 'worktree'; tab: 'overview' | 'tasks' }
  | { kind: 'hardware'; page: 'tree' | 'bom' | 'network' }
  | { kind: 'device' }
type ActiveOperation = {
  id: string
  kind: string
  label: string
  status: api.OperationStatus | null
}
type CompilePrompt = {
  message: string
  flow: 'compare' | 'rebuild'
  context: {
    workbenchId: string
    worktreeId: string
    deviceId: string
  }
}
type DeviceContextRef = {
  workbenchId: string
  worktreeId: string
  deviceId: string
}

const worktreeKey = (workbenchId: string, worktreeId: string) => `${workbenchId}:${worktreeId}`

const displayError = (error: unknown) => {
  if (error instanceof api.WorkbenchApiError) return `${error.code}: ${error.message}`
  return error instanceof Error ? error.message : 'Unexpected operation failure'
}

const newOperationId = () =>
  typeof crypto !== 'undefined' && 'randomUUID' in crypto
    ? crypto.randomUUID()
    : `op-${Date.now()}-${Math.random().toString(16).slice(2)}`

const normalizeProjectPath = (path: string) =>
  path.trim().replaceAll('/', '\\').replace(/\\+$/, '').toLowerCase()

const findHardwareNode = (
  nodes: api.HardwareConfigurationNode[],
  id: string,
): api.HardwareConfigurationNode | null => {
  for (const node of nodes) {
    if (node.id === id) return node
    const nested = findHardwareNode(node.children, id)
    if (nested) return nested
  }
  return null
}

function NewWorktreeDialog({
  workbench,
  busy,
  operationStatus,
  onDismissOperation,
  onClose,
  onCreate,
}: {
  workbench: api.Workbench
  busy: boolean
  operationStatus: api.OperationStatus | null
  onDismissOperation: () => void
  onClose: () => void
  onCreate: (name: string, branch: string, startPoint?: string) => Promise<void>
}) {
  const [name, setName] = useState('')
  const [branch, setBranch] = useState('')
  const [startPoint, setStartPoint] = useState('master')
  const valid = Boolean(name.trim() && branch.trim())
  return (
    <div className="fixed inset-0 z-50 grid place-items-center bg-black/55 p-5 backdrop-blur-[2px]">
      <div className="w-full max-w-[500px] overflow-hidden rounded-xl border bg-card shadow-2xl" style={{ borderColor: 'var(--border)' }}>
        <div className="flex items-center gap-3 border-b px-5 py-4" style={{ borderColor: 'var(--border)' }}>
          <GitBranch className="h-4 w-4 text-chart-4" />
          <div className="flex-1">
            <h2 className="text-sm font-semibold">New linked worktree</h2>
            <p className="text-[10px] text-muted-foreground">{workbench.name} · complete editable checkout</p>
          </div>
          <button className="icon-button" onClick={onClose} disabled={busy}><X className="h-4 w-4" /></button>
        </div>
        <div className="space-y-4 p-5">
          {busy && (
            <div className="flex items-start gap-2 rounded-lg border border-chart-2/30 bg-chart-2/10 p-3 text-[10px]" data-creation-progress aria-live="polite">
              <Loader2 className="mt-0.5 h-3.5 w-3.5 shrink-0 animate-spin text-chart-2" />
              <div className="min-w-0">
                <div className="font-medium">Creating linked worktree…</div>
                <div className="mt-0.5 text-muted-foreground">Preparing the checkout and Git branch. This may take a little while.</div>
              </div>
            </div>
          )}
          <label className="field-label">
            <span>Worktree name</span>
            <input className="field-input" value={name} onChange={event => setName(event.target.value)} placeholder="Commissioning changes" autoFocus />
          </label>
          <label className="field-label">
            <span>Branch</span>
            <input className="field-input font-mono" value={branch} onChange={event => setBranch(event.target.value)} placeholder="feature/commissioning" />
          </label>
          <label className="field-label">
            <span>Start point</span>
            <input className="field-input font-mono" value={startPoint} onChange={event => setStartPoint(event.target.value)} placeholder="master" />
          </label>
        </div>
        <div className="flex items-center justify-between gap-2 border-t bg-muted/25 px-5 py-3" style={{ borderColor: 'var(--border)' }}>
          <OperationStatusLine status={operationStatus} fallback={busy ? 'Creating linked worktree…' : undefined} onDismiss={onDismissOperation} />
          <div className="flex gap-2">
            <button className="secondary-button" onClick={onClose} disabled={busy}>Cancel</button>
            <button className="primary-button" disabled={!valid || busy} onClick={() => onCreate(name.trim(), branch.trim(), startPoint.trim() || undefined)}>
              {busy && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
              Create worktree
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}

function DeleteWorkbenchDialog({
  workbench,
  busy,
  onClose,
  onDelete,
}: {
  workbench: api.Workbench
  busy: boolean
  onClose: () => void
  onDelete: () => void
}) {
  return (
    <div className="fixed inset-0 z-50 grid place-items-center bg-black/60 p-5 backdrop-blur-[2px]">
      <div className="w-full max-w-[520px] overflow-hidden rounded-xl border bg-card shadow-2xl" style={{ borderColor: 'var(--border)' }}>
        <div className="flex items-center gap-3 border-b px-5 py-4" style={{ borderColor: 'var(--border)' }}>
          <div className="grid h-9 w-9 place-items-center rounded-lg bg-red-500/10">
            <Trash2 className="h-4 w-4 text-red-500" />
          </div>
          <div className="flex-1">
            <h2 className="text-sm font-semibold">Delete “{workbench.name}”?</h2>
            <p className="text-[10px] text-muted-foreground">This action cannot be undone</p>
          </div>
          <button className="icon-button" onClick={onClose} disabled={busy}><X className="h-4 w-4" /></button>
        </div>
        <div className="space-y-3 p-5">
          <p className="text-[10px] leading-relaxed text-muted-foreground">
            This permanently deletes the workbench directory — all linked worktrees, PLC source and Git history, knowledge databases, and saved chat sessions.
          </p>
          <div className="break-all rounded-lg border bg-muted/25 p-3 font-mono text-[9px]" style={{ borderColor: 'var(--border)' }}>
            {workbench.rootPath}
          </div>
        </div>
        <div className="flex justify-end gap-2 border-t bg-muted/25 px-5 py-3" style={{ borderColor: 'var(--border)' }}>
          <button className="secondary-button" onClick={onClose} disabled={busy}>Cancel</button>
          <button className="primary-button bg-red-600 hover:bg-red-500" onClick={onDelete} disabled={busy}>
            {busy && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
            Delete permanently
          </button>
        </div>
      </div>
    </div>
  )
}

function DeleteWorktreeDialog({
  workbench,
  worktree,
  busy,
  onClose,
  onDelete,
}: {
  workbench: api.Workbench
  worktree: api.WorkbenchRegistration
  busy: boolean
  onClose: () => void
  onDelete: () => void
}) {
  return (
    <div className="fixed inset-0 z-50 grid place-items-center bg-black/60 p-5 backdrop-blur-[2px]">
      <div className="w-full max-w-[520px] overflow-hidden rounded-xl border bg-card shadow-2xl" style={{ borderColor: 'var(--border)' }}>
        <div className="flex items-center gap-3 border-b px-5 py-4" style={{ borderColor: 'var(--border)' }}>
          <div className="grid h-9 w-9 place-items-center rounded-lg bg-red-500/10">
            <Trash2 className="h-4 w-4 text-red-500" />
          </div>
          <div className="flex-1">
            <h2 className="text-sm font-semibold">Remove “{worktree.name}”?</h2>
            <p className="text-[10px] text-muted-foreground">This deletes the linked checkout and its local device context.</p>
          </div>
          <button className="icon-button" onClick={onClose} disabled={busy}><X className="h-4 w-4" /></button>
        </div>
        <div className="space-y-3 p-5">
          <p className="text-[10px] leading-relaxed text-muted-foreground">
            The shared Git repository and other worktrees stay intact. Any uncommitted files in this worktree will be discarded.
          </p>
          <div className="rounded-lg border bg-muted/25 p-3 text-[10px]" style={{ borderColor: 'var(--border)' }}>
            <span className="font-medium">{workbench.name}</span>
            <span className="mx-1 text-muted-foreground">/</span>
            <span className="font-mono text-muted-foreground">{worktree.branch}</span>
          </div>
        </div>
        <div className="flex justify-end gap-2 border-t bg-muted/25 px-5 py-3" style={{ borderColor: 'var(--border)' }}>
          <button className="secondary-button" onClick={onClose} disabled={busy}>Cancel</button>
          <button className="primary-button bg-red-600 hover:bg-red-500" onClick={onDelete} disabled={busy}>
            {busy && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
            Remove worktree
          </button>
        </div>
      </div>
    </div>
  )
}

function Metric({
  label,
  value,
  tone = 'neutral',
}: {
  label: string
  value: string | number
  tone?: 'neutral' | 'good' | 'warning' | 'danger'
}) {
  const color = tone === 'good' ? 'text-emerald-500'
    : tone === 'warning' ? 'text-amber-500'
      : tone === 'danger' ? 'text-red-500'
        : 'text-foreground'
  return (
    <div className="rounded-lg border bg-card p-3" style={{ borderColor: 'var(--border)' }}>
      <div className={`text-xl font-semibold tabular-nums ${color}`}>{value}</div>
      <div className="mt-1 text-[9px] uppercase tracking-[0.15em] text-muted-foreground">{label}</div>
    </div>
  )
}

function CompileApprovalDialog({
  prompt,
  busy,
  onCancel,
  onApprove,
}: {
  prompt: CompilePrompt
  busy: boolean
  onCancel: () => void
  onApprove: () => void
}) {
  return (
    <div className="fixed inset-0 z-50 grid place-items-center bg-black/60 p-5 backdrop-blur-[2px]">
      <div className="w-full max-w-[560px] overflow-hidden rounded-xl border bg-card shadow-2xl" style={{ borderColor: 'var(--border)' }}>
        <div className="flex items-center gap-3 border-b px-5 py-4" style={{ borderColor: 'var(--border)' }}>
          <div className="grid h-9 w-9 place-items-center rounded-lg bg-amber-500/10">
            <CloudCog className="h-4 w-4 text-amber-500" />
          </div>
          <div className="flex-1">
            <h2 className="text-sm font-semibold">PLC compile required</h2>
            <p className="text-[10px] text-muted-foreground">The export can retry after compiling the selected PLC.</p>
          </div>
          <button className="icon-button" onClick={onCancel} disabled={busy}><X className="h-4 w-4" /></button>
        </div>
        <div className="space-y-3 p-5">
          <div className="rounded-lg border bg-muted/25 p-3 text-[10px] leading-relaxed" style={{ borderColor: 'var(--border)' }}>
            {prompt.message}
          </div>
          <div className="flex items-start gap-2 rounded-lg bg-amber-500/8 p-3 text-[9px] leading-relaxed text-amber-700 dark:text-amber-300">
            <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
            Compile updates TIA compile state for this PLC. It does not save the project source file.
          </div>
        </div>
        <div className="flex justify-end gap-2 border-t bg-muted/25 px-5 py-3" style={{ borderColor: 'var(--border)' }}>
          <button className="secondary-button" onClick={onCancel} disabled={busy}>Compile manually</button>
          <button className="primary-button" onClick={onApprove} disabled={busy}>
            {busy && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
            Compile and retry
          </button>
        </div>
      </div>
    </div>
  )
}

function ApiKeyDialog({
  onClose,
  onSave,
}: {
  onClose: () => void
  onSave: (apiKey: string) => Promise<void>
}) {
  const [apiKey, setApiKey] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const valid = Boolean(apiKey.trim())
  const save = async () => {
    setBusy(true)
    setError(null)
    try {
      await onSave(apiKey.trim())
    } catch (saveError) {
      setError(displayError(saveError))
      setBusy(false)
    }
  }
  return (
    <div className="fixed inset-0 z-50 grid place-items-center bg-black/60 p-5 backdrop-blur-[2px]">
      <div className="w-full max-w-[500px] overflow-hidden rounded-xl border bg-card shadow-2xl" style={{ borderColor: 'var(--border)' }}>
        <div className="flex items-center gap-3 border-b px-5 py-4" style={{ borderColor: 'var(--border)' }}>
          <div className="grid h-9 w-9 place-items-center rounded-lg bg-chart-2/10">
            <KeyRound className="h-4 w-4 text-chart-2" />
          </div>
          <div className="flex-1">
            <h2 className="text-sm font-semibold">DeepSeek API key</h2>
            <p className="text-[10px] text-muted-foreground">Stored locally in the workbench config; live chats reset on save.</p>
          </div>
          <button className="icon-button" onClick={onClose} disabled={busy}><X className="h-4 w-4" /></button>
        </div>
        <div className="space-y-3 p-5">
          <label className="field-label">
            <span>API key</span>
            <input
              className="field-input font-mono"
              type="password"
              value={apiKey}
              onChange={event => setApiKey(event.target.value)}
              placeholder="sk-..."
              autoFocus
            />
          </label>
          {error && (
            <div className="flex items-start gap-2 rounded-lg bg-red-500/8 p-3 text-[9px] leading-relaxed text-red-700 dark:text-red-300">
              <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
              {error}
            </div>
          )}
        </div>
        <div className="flex justify-end gap-2 border-t bg-muted/25 px-5 py-3" style={{ borderColor: 'var(--border)' }}>
          <button className="secondary-button" onClick={onClose} disabled={busy}>Cancel</button>
          <button className="primary-button" disabled={!valid || busy} onClick={() => void save()}>
            {busy && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
            Save key
          </button>
        </div>
      </div>
    </div>
  )
}

export default function MainStudio() {
  const [workbenches, setWorkbenches] = useState<api.Workbench[]>([])
  const [sessions, setSessions] = useState<api.SessionInfo[]>([])
  const [devicesByWorktree, setDevicesByWorktree] = useState<Record<string, api.DeviceSummary[]>>({})
  const [selection, setSelection] = useState<WorkbenchSelection>({
    workbenchId: null,
    worktreeId: null,
    deviceId: null,
  })
  const [deviceSelection, setDeviceSelection] = useState<DeviceSelectionState | null>(null)
  const [hardwareView, setHardwareView] = useState<api.HardwareConfigurationView | null>(null)
  const [hardwareSelectedNodeId, setHardwareSelectedNodeId] = useState<string | null>(null)
  const [hardwareInspectedNodeId, setHardwareInspectedNodeId] = useState<string | null>(null)
  const [mainView, setMainView] = useState<MainView>({ kind: 'project' })
  const [hardwareBomView, setHardwareBomView] = useState<api.HardwareBomView | null>(null)
  const [hardwareNetworkView, setHardwareNetworkView] = useState<api.HardwareNetworkView | null>(null)
  const selectionRequestId = useRef(0)
  const hardwareRequestId = useRef(0)
  const hardwareBomRequestId = useRef(0)
  const hardwareNetworkRequestId = useRef(0)
  const chatAbortRef = useRef<AbortController | null>(null)
  const [activeTab, setActiveTab] = useState<StudioTab>('overview')
  const [activePage, setActivePage] = useState<'studio' | 'tools'>('studio')
  const [chatTabs, setChatTabs] = useState<ChatTabsState>(() => emptyChatTabs())
  const [shellLayout, setShellLayout] = useState<ShellLayout>(() => {
    try {
      return readShellLayout(window.localStorage)
    } catch {
      return readShellLayout(null)
    }
  })
  const dockResizeRef = useRef<{
    side: DockSide
    startX: number
    startWidth: number
  } | null>(null)
  const [knowledgeSelection, setKnowledgeSelection] = useState<{
    node: api.GraphNode | null
    edge: api.GraphEdge | null
  }>({ node: null, edge: null })
  const [chatBusy, setChatBusy] = useState(false)
  const [pendingConfirmation, setPendingConfirmation] = useState<api.PendingConfirmation | null>(null)
  const resolvedConfirmations = useRef<Set<string>>(new Set())
  const [loading, setLoading] = useState(true)
  const [operation, setOperation] = useState<string | null>(null)
  const [activeOperation, setActiveOperation] = useState<ActiveOperation | null>(null)
  const [fatalError, setFatalError] = useState<string | null>(null)
  const [createWorkbenchOpen, setCreateWorkbenchOpen] = useState(false)
  const [sandboxRoots, setSandboxRoots] = useState<string[]>([])
  const [sandboxDenial, setSandboxDenial] = useState<{ message: string; roots: string[] } | null>(null)
  const [createWorktreeFor, setCreateWorktreeFor] = useState<api.Workbench | null>(null)
  const [deleteWorkbenchFor, setDeleteWorkbenchFor] = useState<api.Workbench | null>(null)
  const [deleteWorktreeFor, setDeleteWorktreeFor] = useState<{
    workbench: api.Workbench
    worktree: api.WorkbenchRegistration
  } | null>(null)
  const [preview, setPreview] = useState<api.ReconciliationPreview | null>(null)
  const [compilePrompt, setCompilePrompt] = useState<CompilePrompt | null>(null)
  const [apiKeyConfigured, setApiKeyConfigured] = useState<boolean | null>(null)
  const [apiBalance, setApiBalance] = useState<api.DeepSeekBalance | null>(null)
  const [apiBalanceBusy, setApiBalanceBusy] = useState(false)
  const [apiKeyDialogOpen, setApiKeyDialogOpen] = useState(false)
  const [relativePath, setRelativePath] = useState('')
  const [lastImport, setLastImport] = useState<api.ImportModifiedResult | null>(null)
  const [blockIndexExpanded, setBlockIndexExpanded] = useState(false)
  const [blockFilter, setBlockFilter] = useState('')
  const [versionControlSelection, setVersionControlSelection] = useState<unknown>(null)
  const [appAssistantOpen, setAppAssistantOpen] = useState(false)
  const [appAssistantRuntime, setAppAssistantRuntime] = useState<api.AppAssistantRuntimeSnapshot | null>(null)

  useEffect(() => {
    setVersionControlSelection(null)
    setLastImport(null)
  }, [selection.workbenchId, selection.worktreeId, selection.deviceId])

  useEffect(() => {
    setAppAssistantRuntime(null)
    if (!selection.workbenchId) return
    let cancelled = false
    void api.getAppAssistantRuntimeState(selection.workbenchId).then(snapshot => {
      if (!cancelled) setAppAssistantRuntime(snapshot)
    }).catch(() => { /* The panel reports service/API availability independently. */ })
    const unsubscribe = typeof EventSource === 'undefined' ? () => {} : api.subscribeAppAssistantRuntime(selection.workbenchId, snapshot => {
      if (!cancelled) setAppAssistantRuntime(snapshot)
    })
    return () => {
      cancelled = true
      unsubscribe()
    }
  }, [selection.workbenchId])

  useEffect(() => {
    try { writeShellLayout(window.localStorage, shellLayout) } catch { /* storage is optional */ }
  }, [shellLayout])

  const toggleDock = useCallback((side: DockSide) => {
    setShellLayout(previous => side === 'left'
      ? { ...previous, leftOpen: !previous.leftOpen }
      : { ...previous, rightOpen: !previous.rightOpen })
  }, [])

  const startDockResize = useCallback((side: DockSide, startX: number) => {
    const startWidth = side === 'left' ? shellLayout.leftWidth : shellLayout.rightWidth
    dockResizeRef.current = { side, startX, startWidth }
  }, [shellLayout.leftWidth, shellLayout.rightWidth])

  useEffect(() => {
    const handlePointerMove = (event: PointerEvent) => {
      const resize = dockResizeRef.current
      if (!resize) return
      const delta = event.clientX - resize.startX
      const nextWidth = resize.side === 'left'
        ? resize.startWidth + delta
        : resize.startWidth - delta
      setShellLayout(previous => resize.side === 'left'
        ? { ...previous, leftWidth: clampDockWidth('left', nextWidth) }
        : { ...previous, rightWidth: clampDockWidth('right', nextWidth) })
    }
    const handlePointerUp = () => { dockResizeRef.current = null }
    window.addEventListener('pointermove', handlePointerMove)
    window.addEventListener('pointerup', handlePointerUp)
    return () => {
      window.removeEventListener('pointermove', handlePointerMove)
      window.removeEventListener('pointerup', handlePointerUp)
    }
  }, [])

  const activeWorkbench = useMemo(
    () => workbenches.find(workbench => workbench.workbenchId === selection.workbenchId) ?? null,
    [selection.workbenchId, workbenches],
  )
  const activeWorktree = useMemo(
    () => activeWorkbench?.worktrees.find(worktree => worktree.worktreeId === selection.worktreeId) ?? null,
    [activeWorkbench, selection.worktreeId],
  )
  // Derived hardware sub-page; null when the main view is not a hardware page.
  const hardwarePage = mainView.kind === 'hardware' ? mainView.page : null
  const knowledgeContext = useMemo<api.KnowledgeGraphContext | null>(
    () => selection.workbenchId && selection.worktreeId && selection.deviceId
      ? { workbenchId: selection.workbenchId, worktreeId: selection.worktreeId, deviceId: selection.deviceId }
      : null,
    [selection.workbenchId, selection.worktreeId, selection.deviceId],
  )
  const deviceView = deviceSelection?.view ?? null
  const deviceSessions = deviceSelection?.sessions ?? []
  const deviceInfo = deviceView?.snapshot ?? null
  const deviceName = deviceInfo?.plcName ?? deviceSelection?.cachedMetadata?.plcName ?? selection.deviceId
  const deviceMeta = deviceInfo?.device ?? null
  const hardwareSelectedNode = useMemo(
    () => hardwareView && (hardwareInspectedNodeId ?? hardwareSelectedNodeId)
      ? findHardwareNode(hardwareView.devices, hardwareInspectedNodeId ?? hardwareSelectedNodeId!)
      : null,
    [hardwareInspectedNodeId, hardwareSelectedNodeId, hardwareView],
  )
  const blocks = useMemo(() => deviceView?.blocks ?? [], [deviceView])
  const sourceObjectCount = deviceView?.sourceObjectCount ?? 0
  const displayedSourceObjectCount = deviceView
    ? sourceObjectCount
    : deviceSelection?.cachedMetadata?.sourceObjectCount ?? 0
  const filteredBlocks = useMemo(() => {
    const query = blockFilter.trim().toLowerCase()
    if (!query) return blocks
    return blocks.filter(block =>
      block.name.toLowerCase().includes(query)
      || block.relativePath.toLowerCase().includes(query)
      || `${block.blockType}${block.number ?? ''}`.toLowerCase().includes(query))
  }, [blocks, blockFilter])
  const activeKnowledge = deviceView?.knowledgeState ?? 'missing'
  const isBrandNewDevice = Boolean(selection.deviceId)
    && deviceView?.snapshot.deviceId === selection.deviceId
    && blocks.length === 0
    && activeKnowledge === 'missing'
  const navigatorKnowledgeState = deviceInfo
    ? { [deviceInfo.deviceId]: activeKnowledge }
    : {}
  const activeOperationId = activeOperation?.id
  const matchingTiaSession = useMemo(() => {
    const source = deviceInfo?.sourceProjectPath
    if (!source) return null
    const normalized = normalizeProjectPath(source)
    return sessions.find(session => session.projectPath
      && normalizeProjectPath(session.projectPath) === normalized) ?? null
  }, [deviceInfo?.sourceProjectPath, sessions])
  const selectedChatContext = useMemo(() => {
    if (!selection.workbenchId || !selection.worktreeId || !selection.deviceId) return null
    return {
      workbenchId: selection.workbenchId,
      worktreeId: selection.worktreeId,
      deviceId: selection.deviceId,
    }
  }, [selection.deviceId, selection.workbenchId, selection.worktreeId])

  const replaceDeviceSessions = useCallback((savedSessions: api.ChatSessionInfo[]) => {
    setDeviceSelection(previous => previous ? { ...previous, sessions: savedSessions } : previous)
  }, [])

  const reloadWorkbenches = useCallback(async () => {
    const values = await api.listWorkbenches()
    setWorkbenches(values)
    return values
  }, [])

  const reloadKeyStatus = useCallback(async () => {
    try {
      const status = await api.getKeyStatus()
      setApiKeyConfigured(status.configured)
      return status
    } catch {
      setApiKeyConfigured(null)
      return null
    }
  }, [])

  const reloadBalance = useCallback(async () => {
    setApiBalanceBusy(true)
    try {
      const balance = await api.getDeepSeekBalance()
      setApiBalance(balance)
      return balance
    } catch {
      setApiBalance(null)
      return null
    } finally {
      setApiBalanceBusy(false)
    }
  }, [])

  const reloadSessions = useCallback(async () => {
    const loadedSessions = await api.getSessions()
    setSessions(loadedSessions)
    return loadedSessions
  }, [])

  const loadStartup = useCallback(async () => {
    setLoading(true)
    setFatalError(null)
    try {
      // TIA sessions come from the engineering server and may hang while TIA is
      // busy; refresh them in the background instead of blocking startup.
      void reloadSessions().catch(() => {})
      const loadedWorkbenches = await reloadWorkbenches()
      if (loadedWorkbenches.length > 0) {
        const first = loadedWorkbenches[0]
        // Keep the API selection in sync with the local project selection so
        // workbench-scoped features (including App Assistant) can resolve the
        // active workbench immediately after startup.
        await api.selectWorkbench(first.workbenchId)
        setSelection({ workbenchId: first.workbenchId, worktreeId: null, deviceId: null })
      }
    } catch (error) {
      setFatalError(displayError(error))
    } finally {
      setLoading(false)
    }
    void reloadKeyStatus().then(status => {
      if (status?.configured) void reloadBalance()
    })
  }, [reloadWorkbenches, reloadSessions, reloadKeyStatus, reloadBalance])

  useEffect(() => { void loadStartup() }, [loadStartup])

  // Knowledge browser selection belongs to one device's graph; drop it when the device changes.
  useEffect(() => {
    setKnowledgeSelection({ node: null, edge: null })
  }, [selection.deviceId])

  useEffect(() => {
    const requestId = ++hardwareRequestId.current
    if (!selection.workbenchId || !selection.worktreeId || selection.deviceId || hardwarePage !== 'tree') {
      setHardwareView(null)
      setHardwareSelectedNodeId(null)
      setHardwareInspectedNodeId(null)
      return
    }

    setHardwareView(null)
    setHardwareSelectedNodeId(null)
    setHardwareInspectedNodeId(null)
    void api.getHardwareConfiguration(selection.workbenchId, selection.worktreeId)
      .then(view => {
        if (hardwareRequestId.current !== requestId) return
        setHardwareView(view)
        setHardwareSelectedNodeId(view.devices[0]?.id ?? null)
        setHardwareInspectedNodeId(view.devices[0]?.id ?? null)
      })
      .catch(error => {
        if (hardwareRequestId.current !== requestId) return
        showErrorToast(`Hardware configuration could not be loaded: ${displayError(error)}`)
        setHardwareView({
          state: 'invalid',
          projectAmlPath: null,
          exportedAt: null,
          devices: [],
          tags: [],
          message: displayError(error),
        })
      })
  }, [hardwarePage, selection.deviceId, selection.workbenchId, selection.worktreeId])

  useEffect(() => {
    const requestId = ++hardwareBomRequestId.current
    if (!selection.workbenchId || !selection.worktreeId || selection.deviceId || hardwarePage !== 'bom') {
      setHardwareBomView(null)
      return
    }

    setHardwareBomView(null)
    void api.getHardwareBom(selection.workbenchId, selection.worktreeId)
      .then(view => {
        if (hardwareBomRequestId.current !== requestId) return
        setHardwareBomView(view)
      })
      .catch(error => {
        if (hardwareBomRequestId.current !== requestId) return
        showErrorToast(`Hardware BOM list could not be loaded: ${displayError(error)}`)
        setHardwareBomView({ state: 'invalid', exportedAt: null, items: [], message: displayError(error) })
      })
  }, [hardwarePage, selection.deviceId, selection.workbenchId, selection.worktreeId])

  useEffect(() => {
    const requestId = ++hardwareNetworkRequestId.current
    if (!selection.workbenchId || !selection.worktreeId || selection.deviceId || hardwarePage !== 'network') {
      setHardwareNetworkView(null)
      return
    }

    setHardwareNetworkView(null)
    void api.getHardwareNetwork(selection.workbenchId, selection.worktreeId)
      .then(view => {
        if (hardwareNetworkRequestId.current !== requestId) return
        setHardwareNetworkView(view)
      })
      .catch(error => {
        if (hardwareNetworkRequestId.current !== requestId) return
        showErrorToast(`Hardware network list could not be loaded: ${displayError(error)}`)
        setHardwareNetworkView({ state: 'invalid', exportedAt: null, nodes: [], message: displayError(error) })
      })
  }, [hardwarePage, selection.deviceId, selection.workbenchId, selection.worktreeId])

  useEffect(() => {
    if (!activeOperationId) return undefined
    let cancelled = false
    let successTimer: number | undefined
    const id = activeOperationId

    const poll = async () => {
      try {
        const status = await api.getOperationStatus(id)
        if (cancelled) return
        setActiveOperation(previous => previous?.id === id ? { ...previous, status } : previous)
        if (status.state === 'failed') {
          window.clearInterval(timer)
          return
        }
        if (status.state === 'succeeded') {
          window.clearInterval(timer)
          successTimer ??= window.setTimeout(() => {
            void api.dismissOperationStatus(id).catch(() => undefined)
            setActiveOperation(previous => previous?.id === id ? null : previous)
          }, 3000)
        }
      } catch (error) {
        if (cancelled) return
        if (error instanceof api.WorkbenchApiError && error.status === 404) {
          setActiveOperation(previous => previous?.id === id ? null : previous)
        }
      }
    }

    const timer = window.setInterval(() => void poll(), 1000)
    void poll()
    return () => {
      cancelled = true
      window.clearInterval(timer)
      if (successTimer) window.clearTimeout(successTimer)
    }
  }, [activeOperationId])

  const beginOperation = useCallback((kind: string, label: string) => {
    const next = { id: newOperationId(), kind, label, status: null }
    setActiveOperation(next)
    return next
  }, [])

  const dismissActiveOperation = useCallback(() => {
    const id = activeOperation?.id
    setActiveOperation(null)
    if (id) void api.dismissOperationStatus(id).catch(() => undefined)
  }, [activeOperation?.id])

  const openCreateWorkbench = useCallback(() => {
    setCreateWorkbenchOpen(true)
    void api.getSandboxRoots()
      .then(result => setSandboxRoots(result.roots))
      .catch(() => setSandboxRoots([]))
  }, [])

  const reloadDeviceSnapshot = useCallback(async (context: {
    workbenchId: string
    worktreeId: string
    deviceId: string
  }) => {
    try {
      const snapshot = await api.getDeviceInfo(context.workbenchId, context.worktreeId, context.deviceId)
      if (snapshot.workbenchId !== context.workbenchId
        || snapshot.worktreeId !== context.worktreeId
        || snapshot.deviceId !== context.deviceId) {
        throw new Error('Device snapshot identity does not match the requested context')
      }
      rememberDeviceSnapshot(snapshot)
      setDeviceSelection(previous => {
        const current = previous?.view?.snapshot
        if (!previous || !current
          || current.workbenchId !== context.workbenchId
          || current.worktreeId !== context.worktreeId
          || current.deviceId !== context.deviceId) return previous
        return { ...previous, view: applyDeviceSnapshot(previous.view, snapshot) }
      })
      return snapshot
    } catch (error) {
      setDeviceSelection(previous => previous
        ? { ...previous, view: retainSnapshotOnError(previous.view, error) }
        : previous)
      showErrorToast(`Offline device state could not be refreshed: ${displayError(error)}`)
      return null
    }
  }, [])

  const selectWorkbench = async (workbench: api.Workbench) => {
    try {
      await api.selectWorkbench(workbench.workbenchId)
      setAppAssistantRuntime(null)
      void api.getAppAssistantRuntimeState(workbench.workbenchId)
        .then(runtimeSnapshot => setAppAssistantRuntime(runtimeSnapshot))
        .catch(() => { /* Runtime refresh is best-effort; selection remains usable. */ })
      setSelection({ workbenchId: workbench.workbenchId, worktreeId: null, deviceId: null })
      setMainView({ kind: 'project' })
      setDeviceSelection(null)
      setChatTabs(emptyChatTabs())
    } catch (error) {
      showErrorToast(displayError(error))
    }
  }

  const selectWorktree = async (workbench: api.Workbench, worktree: api.WorkbenchRegistration) => {
    setOperation('select-worktree')
    try {
      await api.selectWorktree(workbench.workbenchId, worktree.worktreeId)
      const devices = await api.listDevices(workbench.workbenchId, worktree.worktreeId)
      void api.getAppAssistantRuntimeState(workbench.workbenchId)
        .then(runtimeSnapshot => setAppAssistantRuntime(runtimeSnapshot))
        .catch(() => { /* Runtime refresh is best-effort; selection remains usable. */ })
      devices.forEach(device => rememberDeviceSummary(workbench.workbenchId, worktree.worktreeId, device))
      setDevicesByWorktree(previous => ({
        ...previous,
        [worktreeKey(workbench.workbenchId, worktree.worktreeId)]: devices,
      }))
      setSelection({ workbenchId: workbench.workbenchId, worktreeId: worktree.worktreeId, deviceId: null })
      setMainView({ kind: 'worktree', tab: 'overview' })
      setDeviceSelection(null)
      setChatTabs(emptyChatTabs())
    } catch (error) {
      showErrorToast(displayError(error))
    } finally {
      setOperation(null)
    }
  }

  const selectDevice = async (
    workbench: api.Workbench,
    worktree: api.WorkbenchRegistration,
    deviceId: string,
  ) => {
    const requestId = ++selectionRequestId.current
    // Selection is a pure metadata operation: apply it instantly. The snapshot
    // (per-block manifest work) loads in the background and fills the view.
    setSelection({ workbenchId: workbench.workbenchId, worktreeId: worktree.worktreeId, deviceId })
    setMainView({ kind: 'device' })
    const cachedContext = {
      workbenchId: workbench.workbenchId,
      worktreeId: worktree.worktreeId,
      deviceId,
    }
    const cachedSummary = devicesByWorktree[worktreeKey(workbench.workbenchId, worktree.worktreeId)]
      ?.find(device => device.deviceId === deviceId)
    const cachedMetadata = readDeviceMetadata(cachedContext)
      ?? (cachedSummary ? rememberDeviceSummary(workbench.workbenchId, worktree.worktreeId, cachedSummary) : null)
    setDeviceSelection(previous => beginDeviceSelection(previous, deviceId, requestId, cachedMetadata))
    setChatTabs(emptyChatTabs())
    setOperation('select-device')
    try {
      const [, snapshot, savedSessions] = await Promise.all([
        // Keep the shared runtime focus in sync with the instant local
        // selection so the status popover and App Assistant see the device.
        api.selectDevice(workbench.workbenchId, worktree.worktreeId, deviceId),
        api.getDeviceInfo(workbench.workbenchId, worktree.worktreeId, deviceId),
        api.listDeviceSessions(workbench.workbenchId, worktree.worktreeId, deviceId).catch(() => []),
      ])
      if (selectionRequestId.current !== requestId) return
      const runtimeSnapshot = await api.getAppAssistantRuntimeState(workbench.workbenchId).catch(() => null)
      if (selectionRequestId.current !== requestId) return
      if (runtimeSnapshot) setAppAssistantRuntime(runtimeSnapshot)
      if (snapshot.workbenchId !== workbench.workbenchId
        || snapshot.worktreeId !== worktree.worktreeId
        || snapshot.deviceId !== deviceId) {
        throw new Error('Device snapshot identity does not match the requested context')
      }
      setDeviceSelection(previous => previous
        ? completeDeviceSelection(previous, requestId, snapshot, savedSessions)
        : previous)
    } catch (error) {
      if (selectionRequestId.current !== requestId) return
      setDeviceSelection(previous => previous
        ? failDeviceSelection(previous, requestId)
        : previous)
      showErrorToast(displayError(error))
    } finally {
      if (selectionRequestId.current === requestId) setOperation(null)
    }
  }

  const runNavigatorDeviceAction = async (
    workbench: api.Workbench,
    worktree: api.WorkbenchRegistration,
    deviceId: string,
    action: (context: DeviceContextRef) => Promise<void>,
  ) => {
    const context = { workbenchId: workbench.workbenchId, worktreeId: worktree.worktreeId, deviceId }
    await selectDevice(workbench, worktree, deviceId)
    await action(context)
  }

  const selectHardwarePage = (
    workbench: api.Workbench,
    worktree: api.WorkbenchRegistration,
    page: 'tree' | 'bom' | 'network',
  ) => {
    setSelection({ workbenchId: workbench.workbenchId, worktreeId: worktree.worktreeId, deviceId: null })
    setMainView({ kind: 'hardware', page })
    setDeviceSelection(null)
    setChatTabs(emptyChatTabs())
    setActiveTab('overview')
  }

  const selectHardware = (workbench: api.Workbench, worktree: api.WorkbenchRegistration) =>
    selectHardwarePage(workbench, worktree, 'tree')

  const reloadHardware = async (
    workbench: api.Workbench,
    worktree: api.WorkbenchRegistration,
  ) => {
    setOperation('reload-hardware')
    const op = beginOperation('reload-hardware', 'Reloading hardware configuration from TIA...')
    try {
      const result = await api.reloadHardwareConfiguration(workbench.workbenchId, worktree.worktreeId, op.id)
      if (result.warnings?.length) {
        toast.warning(
          `Hardware configuration reloaded with ${result.warnings.length} warning(s) (${result.deviceCount} device(s) exported).`,
        )
      } else {
        toast.success(`Hardware configuration reloaded (${result.deviceCount} device(s)).`)
      }
    } catch (error) {
      showErrorToast(displayError(error))
    } finally {
      setOperation(null)
    }
  }

  const compareHardware = async (
    workbench: api.Workbench,
    worktree: api.WorkbenchRegistration,
  ) => {
    setOperation('compare-hardware')
    const op = beginOperation('compare-hardware', 'Comparing hardware configuration with TIA...')
    try {
      const result = await api.compareHardwareConfiguration(workbench.workbenchId, worktree.worktreeId, op.id)
      if (result.state === 'in-sync') {
        toast.success(result.message)
      } else {
        toast.warning(result.message)
        if (window.confirm(
          'Replace the saved hardware AML files with the staged TIA export? This updates the hardware baseline and creates a Git commit.',
        )) {
          setOperation('overwrite-hardware')
          const overwriteOp = beginOperation(
            'overwrite-hardware',
            'Applying staged hardware configuration...',
          )
          const applied = await api.overwriteHardwareConfiguration(
            workbench.workbenchId,
            worktree.worktreeId,
            true,
            overwriteOp.id,
          )
          if (
            selection.workbenchId === workbench.workbenchId
            && selection.worktreeId === worktree.worktreeId
            && selection.deviceId === null
          ) {
            const refreshed = await api.getHardwareConfiguration(
              workbench.workbenchId,
              worktree.worktreeId,
            )
            setHardwareView(refreshed)
            setHardwareSelectedNodeId(refreshed.devices[0]?.id ?? null)
            setHardwareInspectedNodeId(refreshed.devices[0]?.id ?? null)
          }
          toast.success(`Saved hardware configuration updated (${applied.artifactCount} artifact(s)).`)
        }
      }
    } catch (error) {
      showErrorToast(displayError(error))
    } finally {
      setOperation(null)
    }
  }

  const createWorkbench = async (values: {
    name: string
    rootPath?: string
    engineeringSessionId?: number
    engineeringProjectPath?: string
  }) => {
    setOperation('create-workbench')
    const op = beginOperation('create-workbench', 'Preparing workbench storage...')
    try {
      const created = await api.createWorkbench(
        values.name,
        values.engineeringSessionId ?? null,
        values.engineeringProjectPath ?? null,
        values.rootPath,
        op.id,
      )
      const valuesAfterCreate = await reloadWorkbenches()
      const workbench = valuesAfterCreate.find(value => value.workbenchId === created.workbenchId) ?? created
      setCreateWorkbenchOpen(false)
      await selectWorkbench(workbench)
      const master = workbench.worktrees.find(worktree => worktree.branch === 'master') ?? workbench.worktrees[0]
      if (master) await selectWorktree(workbench, master)
      toast.success(`Workbench “${workbench.name}” created`)
    } catch (error) {
      if (error instanceof api.WorkbenchApiError && error.code === 'SANDBOX_PATH_DENIED') {
        setCreateWorkbenchOpen(false)
        const roots = await api.getSandboxRoots()
          .then(result => result.roots)
          .catch(() => sandboxRoots)
        setSandboxDenial({ message: error.message, roots })
      } else {
        showErrorToast(displayError(error))
      }
    } finally {
      setOperation(null)
    }
  }

  const ensureChatContext = useCallback(async () => {
    if (!selectedChatContext) throw new Error('Select a device before opening chat.')
    await api.selectDevice(
      selectedChatContext.workbenchId,
      selectedChatContext.worktreeId,
      selectedChatContext.deviceId,
    )
  }, [selectedChatContext])

  const refreshChatSessions = useCallback(async () => {
    if (!selectedChatContext) return []
    const savedSessions = await api.listDeviceSessions(
      selectedChatContext.workbenchId,
      selectedChatContext.worktreeId,
      selectedChatContext.deviceId,
    )
    replaceDeviceSessions(savedSessions)
    return savedSessions
  }, [replaceDeviceSessions, selectedChatContext])

  const createChatSession = async () => {
    setChatBusy(true)
    try {
      await ensureChatContext()
      const session = await api.newChatSession()
      setChatTabs(previous => openTab(previous, session))
      setActiveTab('chat')
      await refreshChatSessions()
    } catch (error) {
      showErrorToast(displayError(error))
    } finally {
      setChatBusy(false)
    }
  }

  const activateChatSession = async (sessionId: string) => {
    if (chatTabs.activeId === sessionId) {
      setActiveTab('chat')
      return
    }
    setChatBusy(true)
    try {
      await ensureChatContext()
      const session = await api.loadChatSession(sessionId)
      setChatTabs(previous => openTab(previous, session))
      setActiveTab('chat')
    } catch (error) {
      showErrorToast(displayError(error))
      await refreshChatSessions().catch(() => undefined)
    } finally {
      setChatBusy(false)
    }
  }

  const renameChatSession = async (sessionId: string, title: string) => {
    setChatBusy(true)
    try {
      await ensureChatContext()
      const session = await api.renameChatSession(sessionId, title)
      setChatTabs(previous => renameTab(previous, sessionId, session.header.title?.trim() || title))
      await refreshChatSessions()
    } catch (error) {
      showErrorToast(displayError(error))
    } finally {
      setChatBusy(false)
    }
  }

  const removeChatSession = async (sessionId: string) => {
    setChatBusy(true)
    try {
      await ensureChatContext()
      await api.deleteChatSession(sessionId)
      setChatTabs(previous => closeTab(previous, sessionId))
      await refreshChatSessions()
    } catch (error) {
      showErrorToast(displayError(error))
    } finally {
      setChatBusy(false)
    }
  }

  const exportChatSession = async (sessionId: string) => {
    setChatBusy(true)
    try {
      await ensureChatContext()
      const result = await api.exportChatSession(sessionId)
      toast.success(`Session exported to ${result.path}`)
    } catch (error) {
      showErrorToast(displayError(error))
    } finally {
      setChatBusy(false)
    }
  }

  const sendChatMessage = async (sessionId: string, message: string) => {
    setChatBusy(true)
    setChatTabs(previous => appendLocalUserMessage(previous, sessionId, message))
    const controller = new AbortController()
    chatAbortRef.current = controller
    try {
      await ensureChatContext()
      if (chatTabs.activeId !== sessionId) await api.loadChatSession(sessionId)
      let turnFailed = false
      await api.sendChatMessage(message, event => {
        if (event.kind === 'progress') {
          setChatTabs(previous => appendProgressMessage(previous, sessionId, event.delta))
        } else if (event.kind === 'content' || event.kind === 'reasoning') {
          setChatTabs(previous => appendAssistantDelta(previous, sessionId, event.delta))
        } else if (event.kind === 'error') {
          turnFailed = true
          setChatTabs(previous => appendProgressMessage(previous, sessionId, `Error: ${event.delta}`))
        } else if (event.kind === 'meta') {
          setChatTabs(previous => setTurnMeta(previous, sessionId, event.usage ?? null, event.hitRoundCap ?? false, event.toolCalls ?? null))
        }
      }, controller.signal)
      if (turnFailed) {
        // The server ended the turn with an error; keep the local view (user message,
        // streamed text, error note) instead of swapping in an older persisted session.
        await refreshChatSessions().catch(() => undefined)
        return
      }
      const session = await api.loadChatSession(sessionId)
      setChatTabs(previous => openTab(previous, session))
      await refreshChatSessions()
    } catch (error) {
      // Aborting the fetch also cancels the server-side generation via the request token;
      // keep whatever partial text streamed in and mark the turn as stopped.
      if (controller.signal.aborted) {
        setChatTabs(previous => appendProgressMessage(previous, sessionId, 'Generation stopped by user.'))
      } else {
        showErrorToast(displayError(error))
      }
    } finally {
      if (chatAbortRef.current === controller) chatAbortRef.current = null
      setChatBusy(false)
    }
  }

  const stopChatGeneration = useCallback(() => {
    chatAbortRef.current?.abort()
  }, [])

  // Destructive chat tool calls (import_block, vc_restore, save_project) park server-side
  // waiting for an approve/deny decision, announced as JSON entries in /api/logs.
  // Poll them only while a turn runs; show the first unresolved one as a card.
  useEffect(() => {
    if (!chatBusy) return undefined
    let cancelled = false
    const poll = async () => {
      try {
        const lines = await api.getLogs()
        if (cancelled) return
        for (const line of lines) {
          try {
            const entry = JSON.parse(line) as { kind?: string; id?: string; toolName?: string; arguments?: string }
            if (entry.kind === 'confirmation' && typeof entry.id === 'string'
              && !resolvedConfirmations.current.has(entry.id)) {
              setPendingConfirmation(previous => previous?.id === entry.id
                ? previous
                : { id: entry.id!, toolName: entry.toolName ?? '', arguments: entry.arguments ?? '' })
              return
            }
          } catch { /* non-JSON log line */ }
        }
      } catch { /* logs unavailable; retry next tick */ }
    }
    void poll()
    const timer = window.setInterval(() => void poll(), 1000)
    return () => {
      cancelled = true
      window.clearInterval(timer)
    }
  }, [chatBusy])

  const decideConfirmation = async (decision: 'allowOnce' | 'deny') => {
    const pending = pendingConfirmation
    if (!pending) return
    resolvedConfirmations.current.add(pending.id)
    setPendingConfirmation(null)
    try {
      await api.confirmTool(pending.id, decision)
    } catch { /* expired or already resolved server-side */ }
  }

  const continueChat = async (sessionId: string) => {
    setChatBusy(true)
    try {
      await ensureChatContext()
      if (chatTabs.activeId !== sessionId) await api.loadChatSession(sessionId)
      await api.grantChatRounds(6)
    } catch (error) {
      showErrorToast(displayError(error))
      setChatBusy(false)
      return
    }
    setChatBusy(false)
    await sendChatMessage(sessionId, 'continue')
  }

  const createWorktree = async (name: string, branch: string, startPoint?: string) => {
    if (!createWorktreeFor) return
    setOperation('create-worktree')
    const op = beginOperation('create-worktree', 'Creating linked worktree...')
    try {
      await api.createWorktree(createWorktreeFor.workbenchId, name, branch, startPoint, op.id)
      const refreshed = await reloadWorkbenches()
      const workbench = refreshed.find(value => value.workbenchId === createWorktreeFor.workbenchId)
      setCreateWorktreeFor(null)
      if (workbench) {
        const worktree = workbench.worktrees.find(value => value.branch === branch)
        if (worktree) await selectWorktree(workbench, worktree)
      }
      toast.success(`Linked worktree “${name}” created`)
    } catch (error) {
      showErrorToast(displayError(error))
    } finally {
      setOperation(null)
    }
  }

  const stageRefresh = async (allowCompile = false, contextOverride?: DeviceContextRef) => {
    const context = contextOverride ?? (selection.workbenchId && selection.worktreeId && selection.deviceId
      ? { workbenchId: selection.workbenchId, worktreeId: selection.worktreeId, deviceId: selection.deviceId }
      : null)
    if (!context) return
    setCompilePrompt(null)
    setOperation('stage-refresh')
    const op = beginOperation(
      'stage-refresh',
      allowCompile
        ? 'Compiling selected PLC and retrying export...'
        : 'Exporting live PLC to temporary comparison staging...',
    )
    try {
      await api.stageDeviceRefresh(
        context.workbenchId,
        context.worktreeId,
        context.deviceId,
        op.id,
        allowCompile,
      )
      setPreview(await api.previewDeviceRefresh(context.workbenchId, context.worktreeId, context.deviceId))
      toast.success('Comparison ready; no tracked files changed.')
    } catch (error) {
      if (!allowCompile
        && error instanceof api.WorkbenchApiError
        && error.code === 'PLC_COMPILE_REQUIRED') {
        setCompilePrompt({ message: error.message, flow: 'compare', context })
      } else {
        showErrorToast(displayError(error))
      }
    } finally {
      setOperation(null)
    }
  }

  const openProjectInTia = async (contextOverride?: DeviceContextRef, withUI = true) => {
    const context = contextOverride ?? (activeWorkbench && activeWorktree && selection.deviceId
      ? {
        workbenchId: activeWorkbench.workbenchId,
        worktreeId: activeWorktree.worktreeId,
        deviceId: selection.deviceId,
      }
      : null)
    if (!context) return
    setOperation('open-tia-project')
    const op = beginOperation('open-tia-project', 'Opening registered project in TIA Portal...')
    try {
      await api.openDeviceProject(context.workbenchId, context.worktreeId, context.deviceId, op.id, withUI)
      toast.success(withUI ? 'Registered project opened in TIA Portal' : 'Registered project opened headless')
    } catch (error) {
      // A live session query is only warranted now: if the project is already
      // open in a running TIA instance, surface the re-attach option instead of
      // leaving the user with a bare "another user has it open" error.
      const live = await api.getSessions().catch(() => null)
      if (live) setSessions(live)
      const source = context.workbenchId === selection.workbenchId
        && context.worktreeId === selection.worktreeId
        && context.deviceId === selection.deviceId
        ? deviceInfo?.sourceProjectPath
        : null
      const match = source
        ? live?.find(session => session.projectPath
            && normalizeProjectPath(session.projectPath) === normalizeProjectPath(source))
        : null
      if (match) {
        showErrorToast(`Project is already open in TIA (PID ${match.id}) — use Re-attach TIA instance instead.`)
      } else {
        showErrorToast(displayError(error))
      }
    } finally {
      setOperation(null)
    }
  }

  const attachTiaInstance = async (sessionId: number) => {
    if (!activeWorkbench || !activeWorktree || !selection.deviceId) return
    setOperation('attach-tia-instance')
    const op = beginOperation('attach-tia-instance', 'Attaching to running TIA Portal instance...')
    try {
      await api.attachDeviceProject(
        activeWorkbench.workbenchId,
        activeWorktree.worktreeId,
        selection.deviceId,
        sessionId,
        op.id,
      )
      toast.success('Attached to the running TIA Portal instance')
    } catch (error) {
      showErrorToast(displayError(error))
    } finally {
      setOperation(null)
    }
  }

  const applyRefresh = async (approvedPaths: string[], commitTitle?: string) => {
    if (!selection.workbenchId || !selection.worktreeId || !selection.deviceId || !preview) return
    const context = { workbenchId: selection.workbenchId, worktreeId: selection.worktreeId, deviceId: selection.deviceId }
    setOperation('apply-refresh')
    const op = beginOperation('apply-refresh', 'Applying approved refresh...')
    try {
      const result = await api.applyDeviceRefresh(context.workbenchId, context.worktreeId, context.deviceId, preview.previewId, approvedPaths, op.id, commitTitle)
      setPreview(null)
      await reloadDeviceSnapshot(context)
      if (result.error) {
        toast.warning(`Files updated, commit failed: ${result.error}`)
      } else if (result.commitSha) {
        toast.success(`Refresh committed ${result.commitSha.slice(0, 8)}`)
      } else {
        toast.success('PLC source is already current')
      }
    } catch (error) {
      const message = displayError(error)
      if (error instanceof api.WorkbenchApiError && error.code.includes('STALE')) {
        setPreview(null)
        showErrorToast('The preview is stale. Stage and review the export again.')
      } else {
        showErrorToast(message)
      }
    } finally {
      setOperation(null)
    }
  }

  const updateKnowledge = async (rebuild = false, contextOverride?: DeviceContextRef) => {
    const context = contextOverride ?? (selection.workbenchId && selection.worktreeId && selection.deviceId
      ? { workbenchId: selection.workbenchId, worktreeId: selection.worktreeId, deviceId: selection.deviceId }
      : null)
    if (!context) return
    setOperation(rebuild ? 'rebuild-knowledge' : 'update-knowledge')
    const op = beginOperation(
      rebuild ? 'rebuild-knowledge' : 'update-knowledge',
      rebuild ? 'Rebuilding device knowledge...' : 'Updating device knowledge...',
    )
    try {
      const result = rebuild
        ? await api.rebuildDeviceKnowledge(context.workbenchId, context.worktreeId, context.deviceId, op.id)
        : await api.updateDeviceKnowledge(context.workbenchId, context.worktreeId, context.deviceId, op.id)
      await reloadDeviceSnapshot(context)
      toast.success(`${result.updatedComponents.length} component${result.updatedComponents.length === 1 ? '' : 's'} updated`)
    } catch (error) {
      showErrorToast(displayError(error))
    } finally {
      setOperation(null)
    }
  }

  const prepareEdit = async () => {
    if (!selection.workbenchId || !selection.worktreeId || !selection.deviceId || !relativePath.trim()) return
    const context = { workbenchId: selection.workbenchId, worktreeId: selection.worktreeId, deviceId: selection.deviceId }
    setOperation('prepare-edit')
    try {
      await api.prepareDeviceEdit(context.workbenchId, context.worktreeId, context.deviceId, relativePath.trim())
      await reloadDeviceSnapshot(context)
      toast.success('PLC source prepared for editing in this worktree.')
    } catch (error) {
      showErrorToast(displayError(error))
    } finally {
      setOperation(null)
    }
  }

  const importSource = async () => {
    if (!selection.workbenchId || !selection.worktreeId || !selection.deviceId || !relativePath.trim()) return
    const context = { workbenchId: selection.workbenchId, worktreeId: selection.worktreeId, deviceId: selection.deviceId }
    setOperation('import-source')
    const op = beginOperation('import-source', 'Importing PLC source...')
    try {
      const result = await api.importDeviceSource(context.workbenchId, context.worktreeId, context.deviceId, relativePath.trim(), op.id)
      setLastImport(result)
      await reloadDeviceSnapshot(context)
      if (result.importSucceeded && result.compileState.toLowerCase().includes('success')) {
        toast.success('PLC source imported and compiled; source file retained')
      } else {
        toast.warning(result.error || `Compile state: ${result.compileState}`)
      }
    } catch (error) {
      showErrorToast(displayError(error))
    } finally {
      setOperation(null)
    }
  }

  const mergeIntoMaster = async (targetContext?: {
    workbench: api.Workbench
    worktree: api.WorkbenchRegistration
  }) => {
    const workbench = targetContext?.workbench ?? activeWorkbench
    const worktree = targetContext?.worktree ?? activeWorktree
    if (!workbench || !worktree || worktree.branch === 'master') return
    setActiveTab('git')
    toast.info(`Validate and merge ${worktree.branch} from the Version control workspace.`)
  }

  const saveApiKey = async (apiKey: string) => {
    await api.saveApiKey(apiKey)
    const status = await reloadKeyStatus()
    if (status?.configured) await reloadBalance()
    setApiKeyDialogOpen(false)
    toast.success('DeepSeek API key saved; live chats reset')
  }

  const deleteWorkbench = async () => {
    if (!deleteWorkbenchFor) return
    const workbench = deleteWorkbenchFor
    setOperation('delete-workbench')
    const op = beginOperation('delete-workbench', 'Deleting workbench...')
    try {
      await api.deleteWorkbench(workbench.workbenchId, op.id)
      setDeleteWorkbenchFor(null)
      if (selection.workbenchId === workbench.workbenchId) {
        setSelection({ workbenchId: null, worktreeId: null, deviceId: null })
        setMainView({ kind: 'project' })
        setDeviceSelection(null)
        setChatTabs(emptyChatTabs())
      }
      await reloadWorkbenches()
      toast.success(`Workbench “${workbench.name}” deleted`)
    } catch (error) {
      showErrorToast(displayError(error))
    } finally {
      setOperation(null)
    }
  }

  const handleAppAssistantWorkbenchCreated = async (workbenchId: string) => {
    const refreshed = await reloadWorkbenches()
    const workbench = refreshed.find(value => value.workbenchId === workbenchId)
    if (!workbench) throw new Error('The new workbench was created but is not visible in the project list yet.')
    await selectWorkbench(workbench)
    const master = workbench.worktrees.find(value => value.branch === 'master') ?? workbench.worktrees[0]
    if (master) await selectWorktree(workbench, master)
    toast.success(`Workbench “${workbench.name}” created by Workbench Assistant`)
  }

  const deleteWorktree = async () => {
    if (!deleteWorktreeFor) return
    const { workbench, worktree } = deleteWorktreeFor
    setOperation('delete-worktree')
    const op = beginOperation('delete-worktree', 'Removing linked worktree...')
    try {
      await api.deleteWorktree(workbench.workbenchId, worktree.worktreeId, op.id)
      setDeleteWorktreeFor(null)
      if (selection.workbenchId === workbench.workbenchId && selection.worktreeId === worktree.worktreeId) {
        setSelection({ workbenchId: workbench.workbenchId, worktreeId: null, deviceId: null })
        setMainView({ kind: 'project' })
        setDeviceSelection(null)
        setChatTabs(emptyChatTabs())
      }
      await reloadWorkbenches()
      toast.success(`Worktree “${worktree.name}” removed`)
    } catch (error) {
      showErrorToast(displayError(error))
    } finally {
      setOperation(null)
    }
  }

  const bootstrapDevice = async () => {
    if (!selection.workbenchId || !selection.worktreeId || !selection.deviceId) return
    const context = { workbenchId: selection.workbenchId, worktreeId: selection.worktreeId, deviceId: selection.deviceId }
    setOperation('bootstrap-worktree')
    const op = beginOperation('bootstrap-worktree', 'Generating PLC contexts: export, baseline commit, knowledge ingest...')
    try {
      await api.bootstrapWorktree(context.workbenchId, context.worktreeId, context.deviceId, op.id)
      await reloadDeviceSnapshot(context)
      setActiveTab('chat')
      toast.success('PLC context ready — start chatting to explore your project.')
    } catch (error) {
      showErrorToast(displayError(error))
    } finally {
      setOperation(null)
    }
  }

  const [rebuildArmed, setRebuildArmed] = useState(false)

  const rebuildProject = async (allowCompile = false, contextOverride?: DeviceContextRef) => {
    const context = contextOverride ?? (selection.workbenchId && selection.worktreeId && selection.deviceId
      ? { workbenchId: selection.workbenchId, worktreeId: selection.worktreeId, deviceId: selection.deviceId }
      : null)
    if (!context) return
    setCompilePrompt(null)
    setOperation('bootstrap-device')
    const op = beginOperation(
      'bootstrap-device',
      allowCompile
        ? 'Compiling PLC and retrying full project rebuild...'
        : 'Rebuilding project: full export, baseline commit, knowledge ingest...',
    )
    try {
      await api.bootstrapWorktree(context.workbenchId, context.worktreeId, context.deviceId, op.id, 'rebuild: full export', allowCompile)
      await reloadDeviceSnapshot(context)
      toast.success('Project rebuilt from TIA — baseline and knowledge refreshed.')
    } catch (error) {
      if (!allowCompile
        && error instanceof api.WorkbenchApiError
        && error.code === 'PLC_COMPILE_REQUIRED') {
        setCompilePrompt({ message: error.message, flow: 'rebuild', context })
      } else {
        showErrorToast(displayError(error))
      }
    } finally {
      setOperation(null)
    }
  }

  const tabs: Array<{ id: StudioTab; label: string; icon: typeof Boxes }> = [
    { id: 'overview', label: 'Device overview', icon: Cpu },
    { id: 'chat', label: 'AI chat', icon: MessageSquare },
    { id: 'source', label: 'PLC source', icon: Code2 },
    { id: 'knowledge', label: 'Knowledge', icon: Database },
    { id: 'git', label: 'Version control', icon: GitBranch },
  ]

  const hardwareTabs: Array<{ id: 'tree' | 'bom' | 'network'; label: string; icon: typeof Boxes }> = [
    { id: 'tree', label: 'Hardware configuration', icon: CircuitBoard },
    { id: 'bom', label: 'BOM list', icon: ClipboardList },
    { id: 'network', label: 'Network list', icon: Network },
  ]

  return (
    <div className="flex h-screen min-h-[620px] flex-col overflow-hidden bg-background text-foreground">
      <header className="flex h-12 shrink-0 items-center border-b bg-card px-3" style={{ borderColor: 'var(--border)' }}>
        <button
          data-dock-toggle="left"
          className="icon-button mr-1"
          aria-label={shellLayout.leftOpen ? 'Hide workbench project tree' : 'Show workbench project tree'}
          title={shellLayout.leftOpen ? 'Hide workbench project tree' : 'Show workbench project tree'}
          onClick={() => toggleDock('left')}
        >
          {shellLayout.leftOpen ? <PanelLeftClose className="h-3.5 w-3.5" /> : <PanelLeftOpen className="h-3.5 w-3.5" />}
        </button>
        <div className="flex-1" />
          <div className="flex items-center gap-2">
          {activeOperation && (
            <div className="max-w-[360px] rounded-full border px-2 py-1" style={{ borderColor: 'var(--border)' }}>
              <OperationStatusLine
                status={activeOperation.status}
                fallback={activeOperation.label}
                onDismiss={dismissActiveOperation}
              />
            </div>
          )}
          <button
            className={`icon-button ${activePage === 'tools' ? 'bg-accent text-foreground' : ''}`}
            aria-label="Open MCP tools helper"
            title="Open MCP tools helper"
            aria-pressed={activePage === 'tools'}
            onClick={() => setActivePage(previous => previous === 'tools' ? 'studio' : 'tools')}
          >
            <Wrench className="h-3.5 w-3.5" />
          </button>
          {selection.workbenchId && (
            <button
              className={`icon-button ${appAssistantOpen ? 'bg-accent text-foreground' : ''}`}
              aria-label="Open Workbench Assistant"
              title="Open Workbench Assistant"
              aria-pressed={appAssistantOpen}
              onClick={() => setAppAssistantOpen(previous => !previous)}
            >
              <Sparkles className="h-3.5 w-3.5" />
            </button>
          )}
          <button
            data-dock-toggle="right"
            className="icon-button"
            aria-label={shellLayout.rightOpen ? 'Hide context dock' : 'Show context dock'}
            title={shellLayout.rightOpen ? 'Hide context dock' : 'Show context dock'}
            onClick={() => toggleDock('right')}
          >
            {shellLayout.rightOpen ? <PanelRightClose className="h-3.5 w-3.5" /> : <PanelRightOpen className="h-3.5 w-3.5" />}
          </button>
          <ThemeToggle />
        </div>
      </header>

      {activePage === 'tools' ? (
        <McpToolsHelper onClose={() => setActivePage('studio')} />
      ) : <div className="flex min-h-0 flex-1">
        <div
          data-dock="left"
          data-dock-state={shellLayout.leftOpen ? 'open' : 'closed'}
          aria-hidden={!shellLayout.leftOpen}
          className="dock-shell dock-shell-left min-h-0 shrink-0"
          style={{ width: shellLayout.leftOpen ? shellLayout.leftWidth : 0 }}
        >
          <WorkbenchNavigator
            workbenches={workbenches}
            devicesByWorktree={devicesByWorktree}
            selection={selection}
            viewKind={mainView.kind}
            knowledgeState={navigatorKnowledgeState}
            loading={loading}
            onCreateWorkbench={openCreateWorkbench}
            onCreateWorktree={setCreateWorktreeFor}
            onRefresh={() => void loadStartup()}
            onSelectWorkbench={workbench => void selectWorkbench(workbench)}
            onSelectWorktree={(workbench, worktree) => void selectWorktree(workbench, worktree)}
            onSelectDevice={(workbench, worktree, deviceId) => void selectDevice(workbench, worktree, deviceId)}
            onSelectHardware={selectHardware}
            onReloadHardware={(workbench, worktree) => void reloadHardware(workbench, worktree)}
            onCompareHardware={(workbench, worktree) => void compareHardware(workbench, worktree)}
            onDeleteWorkbench={workbench => setDeleteWorkbenchFor(workbench)}
            onDeleteWorktree={(workbench, worktree) => setDeleteWorktreeFor({ workbench, worktree })}
            onMergeWorktree={(workbench, worktree) => void mergeIntoMaster({ workbench, worktree })}
            onOpenDevice={(workbench, worktree, deviceId, withUI) => void runNavigatorDeviceAction(
              workbench,
              worktree,
              deviceId,
              context => openProjectInTia(context, withUI),
            )}
            onCompareDevice={(workbench, worktree, deviceId) => void runNavigatorDeviceAction(
              workbench,
              worktree,
              deviceId,
              context => stageRefresh(false, context),
            )}
            onRebuildDevice={(workbench, worktree, deviceId) => {
              if (window.confirm('Rebuild this PLC project from TIA? This updates the baseline and knowledge database.')) {
                void runNavigatorDeviceAction(workbench, worktree, deviceId, context => rebuildProject(false, context))
              }
            }}
            onUpdateKnowledge={(workbench, worktree, deviceId) => void runNavigatorDeviceAction(
              workbench,
              worktree,
              deviceId,
              context => updateKnowledge(false, context),
            )}
            onRebuildKnowledge={(workbench, worktree, deviceId) => void runNavigatorDeviceAction(
              workbench,
              worktree,
              deviceId,
              context => updateKnowledge(true, context),
            )}
          />
        </div>
        <div
          role="separator"
          aria-orientation="vertical"
          aria-label="Resize workbench project tree"
          className="dock-resize-handle"
          data-dock-state={shellLayout.leftOpen ? 'open' : 'closed'}
          onPointerDown={event => startDockResize('left', event.clientX)}
        />

        <main className="flex min-h-0 min-w-0 flex-1 flex-col overflow-hidden">
          {fatalError ? (
            <div className="grid h-full place-items-center p-8">
              <div className="max-w-lg rounded-xl border bg-card p-6 text-center" style={{ borderColor: 'var(--border)' }}>
                <AlertCircle className="mx-auto mb-3 h-8 w-8 text-red-500" />
                <h1 className="text-sm font-semibold">Workbench API unavailable</h1>
                <p className="mt-2 break-words text-[10px] leading-relaxed text-muted-foreground">{fatalError}</p>
                <button className="primary-button mt-4" onClick={() => void loadStartup()}>
                  <RefreshCw className="h-3.5 w-3.5" /> Retry
                </button>
              </div>
            </div>
          ) : !selection.deviceId && selection.worktreeId ? (
            mainView.kind === 'hardware' ? (
            <>
              <div className="flex h-10 shrink-0 items-center gap-1 border-b px-3" style={{ borderColor: 'var(--border)' }}>
                {hardwareTabs.map(tab => {
                  const Icon = tab.icon
                  return (
                    <button
                      key={tab.id}
                      onClick={() => setMainView({ kind: 'hardware', page: tab.id })}
                      className={`flex h-7 items-center gap-1.5 rounded-md px-2.5 text-[9px] transition-colors ${hardwarePage === tab.id ? 'bg-accent text-foreground' : 'text-muted-foreground hover:bg-accent/50 hover:text-foreground'}`}
                    >
                      <Icon className="h-3 w-3" /> {tab.label}
                    </button>
                  )
                })}
                <div className="flex-1" />
              </div>
              <div className="flex min-h-0 min-w-0 flex-1 flex-col">
                {hardwarePage === 'bom' ? (
                  <HardwareBomView view={hardwareBomView} />
                ) : hardwarePage === 'network' ? (
                  <HardwareNetworkView view={hardwareNetworkView} />
                ) : (
                  <HardwareConfigurationView
                    view={hardwareView}
                    selectedNodeId={hardwareSelectedNodeId}
                    inspectedNodeId={hardwareInspectedNodeId}
                    onSelectNode={node => {
                      setHardwareSelectedNodeId(node.id)
                      setHardwareInspectedNodeId(node.id)
                    }}
                    onInspectNode={node => setHardwareInspectedNodeId(node.id)}
                  />
                )}
              </div>
            </>
            ) : (
              <WorktreeLandingPage
                workbenchId={selection.workbenchId!}
                worktreeId={selection.worktreeId}
                tab={mainView.kind === 'worktree' ? mainView.tab : 'overview'}
                onTabChange={tab => setMainView({ kind: 'worktree', tab })}
                onSelectDevice={deviceId => {
                  if (activeWorkbench && activeWorktree) void selectDevice(activeWorkbench, activeWorktree, deviceId)
                }}
              />
            )
          ) : !selection.deviceId && selection.workbenchId ? (
            <ProjectLandingPage
              workbenchId={selection.workbenchId}
              onOpenAssistant={() => setAppAssistantOpen(true)}
              onSelectWorktree={worktreeId => {
                const worktree = activeWorkbench?.worktrees.find(candidate => candidate.worktreeId === worktreeId)
                if (activeWorkbench && worktree) void selectWorktree(activeWorkbench, worktree)
              }}
            />
          ) : !selection.deviceId ? (
            <div className="relative grid h-full place-items-center overflow-hidden p-8">
              <div className="pointer-events-none absolute inset-0 opacity-[0.035]" style={{
                backgroundImage: 'linear-gradient(var(--foreground) 1px, transparent 1px), linear-gradient(90deg, var(--foreground) 1px, transparent 1px)',
                backgroundSize: '36px 36px',
              }} />
              <div className="relative max-w-xl text-center">
                <div className="mx-auto mb-5 grid h-16 w-16 place-items-center rounded-2xl border bg-card shadow-sm" style={{ borderColor: 'var(--border)' }}>
                  <Cpu className="h-7 w-7 text-chart-2" />
                </div>
                <h1 className="text-xl font-semibold tracking-tight">Select a device context</h1>
                <p className="mx-auto mt-2 max-w-md text-[11px] leading-relaxed text-muted-foreground">
                  Choose a workbench, linked worktree, and PLC device. Every source, knowledge, Git, and chat operation is then bound to that exact context.
                </p>
                {workbenches.length === 0 && (
                  <button className="primary-button mt-5" onClick={openCreateWorkbench}>
                    <Plus className="h-3.5 w-3.5" /> Create workbench
                  </button>
                )}
              </div>
            </div>
          ) : (
            <>
              <div className="flex h-10 shrink-0 items-center gap-1 border-b px-3" style={{ borderColor: 'var(--border)' }}>
                {tabs.map(tab => {
                  const Icon = tab.icon
                  return (
                    <button
                      key={tab.id}
                      onClick={() => setActiveTab(tab.id)}
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
                  <div className="mx-auto max-w-6xl space-y-5 p-5">
                    <section className="flex flex-wrap items-start gap-4 rounded-xl border bg-card p-5" style={{ borderColor: 'var(--border)' }}>
                      <div className="grid h-12 w-12 place-items-center rounded-xl bg-chart-2/10">
                        <Cpu className="h-5 w-5 text-chart-2" />
                      </div>
                      <div className="min-w-0 flex-1">
                        <h1 className="text-lg font-semibold">{deviceName}</h1>
                        <p className="mt-0.5 font-mono text-[9px] text-muted-foreground">{deviceInfo?.engineeringIdentity ?? selection.deviceId}</p>
                        {(deviceMeta?.typeIdentifier || deviceMeta?.deviceName) && (
                          <p className="mt-1.5 flex items-center gap-1.5 font-mono text-[10px] text-muted-foreground">
                            <Cpu className="h-3 w-3" />
                            {deviceMeta.typeIdentifier?.replace(/^OrderNumber:/, '') ?? ''}
                            {deviceMeta.typeIdentifier && deviceMeta.deviceName ? ' · ' : ''}
                            {deviceMeta.deviceName ?? ''}
                          </p>
                        )}
                        <div className="mt-3 flex flex-wrap gap-2">
                          <button className="secondary-button" disabled={Boolean(operation)} onClick={() => void openProjectInTia()}>
                            <Server className="h-3.5 w-3.5" /> Open project in TIA
                          </button>
                          {matchingTiaSession && (
                            <button className="secondary-button" disabled={Boolean(operation)} onClick={() => void attachTiaInstance(matchingTiaSession.id)}>
                              <Server className="h-3.5 w-3.5" /> Re-attach TIA instance (PID {matchingTiaSession.id})
                            </button>
                          )}
                          <button className="primary-button" disabled={Boolean(operation)} onClick={() => void stageRefresh()}>
                            <RefreshCw className="h-3.5 w-3.5" /> Compare with TIA
                          </button>
                          {!isBrandNewDevice && (
                            <button
                              className={rebuildArmed ? 'primary-button' : 'secondary-button'}
                              disabled={Boolean(operation)}
                              onClick={() => {
                                if (!rebuildArmed) {
                                  setRebuildArmed(true)
                                  setTimeout(() => setRebuildArmed(false), 4000)
                                  return
                                }
                                setRebuildArmed(false)
                                void rebuildProject()
                              }}
                            >
                              <RotateCw className="h-3.5 w-3.5" /> {rebuildArmed ? 'Confirm full rebuild?' : 'Rebuild project'}
                            </button>
                          )}
                          <button className="secondary-button" disabled={Boolean(operation)} onClick={() => void updateKnowledge(false)}>
                            <Database className="h-3.5 w-3.5" /> Update knowledge
                          </button>
                          {activeWorktree?.branch !== 'master' && (
                            <button className="secondary-button" disabled={Boolean(operation)} onClick={() => void mergeIntoMaster()}>
                              <GitMerge className="h-3.5 w-3.5" /> Merge to master
                            </button>
                          )}
                        </div>
                      </div>
                      <div className="rounded-lg border px-3 py-2" style={{ borderColor: 'var(--border)' }}>
                        <div className="flex items-center gap-2 text-[8px] uppercase tracking-[0.16em] text-muted-foreground">
                          <span>Knowledge</span>
                          <span className="rounded-full bg-emerald-500/10 px-1.5 py-0.5 text-emerald-600 dark:text-emerald-400">Offline ready</span>
                        </div>
                        <div className={`mt-1 flex items-center gap-1.5 text-[10px] font-medium ${
                          activeKnowledge === 'current' ? 'text-emerald-500'
                            : activeKnowledge === 'stale' ? 'text-amber-500'
                              : activeKnowledge === 'failed' ? 'text-red-500'
                                : 'text-muted-foreground'
                        }`}>
                          <Database className="h-3.5 w-3.5" /> {activeKnowledge}
                        </div>
                        <div className="mt-1 text-[8px] text-muted-foreground">
                          Updated {deviceView?.knowledgeUpdatedAt
                            ? new Date(deviceView.knowledgeUpdatedAt).toLocaleString()
                            : 'never'}
                        </div>
                      </div>
                    </section>

                    {isBrandNewDevice && (
                      <section className="flex flex-wrap items-center gap-4 rounded-xl border border-chart-2/40 bg-chart-2/5 p-5">
                        <div className="grid h-10 w-10 place-items-center rounded-lg bg-chart-2/10">
                          <Sparkles className="h-5 w-5 text-chart-2" />
                        </div>
                        <div className="min-w-0 flex-1">
                          <h2 className="text-sm font-semibold">Start by generating the PLC context</h2>
                          <p className="mt-1 text-[10px] leading-relaxed text-muted-foreground">
                            Exports the full PLC from TIA, commits it as the initial baseline, and builds the offline knowledge database — no confirmations needed.
                          </p>
                        </div>
                        <button className="primary-button" disabled={Boolean(operation)} onClick={() => void bootstrapDevice()}>
                          <Sparkles className="h-3.5 w-3.5" /> Generate PLC context
                        </button>
                      </section>
                    )}

                    <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
                      <Metric label="PLC blocks" value={blocks.length} />
                      <Metric label="Source objects" value={displayedSourceObjectCount} />
                      <Metric label="Saved sessions" value={deviceSessions.length} />
                      <Metric label="Knowledge state" value={activeKnowledge} tone={activeKnowledge === 'current' ? 'good' : activeKnowledge === 'failed' ? 'danger' : 'warning'} />
                    </div>

                    <div className="grid gap-4 lg:grid-cols-2">
                      <section className="rounded-xl border bg-card p-5" style={{ borderColor: 'var(--border)' }}>
                        <div className="flex items-center gap-3">
                          <Database className="h-5 w-5 text-chart-2" />
                          <div>
                            <h2 className="text-sm font-semibold">Device-owned knowledge</h2>
                            <p className="text-[9px] text-muted-foreground">No cross-device lifecycle coupling</p>
                          </div>
                        </div>
                        <div className="mt-5 rounded-lg border bg-muted/30 p-4" style={{ borderColor: 'var(--border)' }}>
                          <div className="text-[8px] uppercase tracking-[0.16em] text-muted-foreground">State</div>
                          <div className="mt-2 flex items-center gap-2 text-lg font-semibold capitalize">
                            <CircleDot className={`h-4 w-4 ${activeKnowledge === 'current' ? 'text-emerald-500' : activeKnowledge === 'failed' ? 'text-red-500' : 'text-amber-500'}`} />
                            {activeKnowledge}
                          </div>
                          <div className="mt-2 text-[9px] text-muted-foreground">
                            Last updated: {deviceView?.knowledgeUpdatedAt
                              ? new Date(deviceView.knowledgeUpdatedAt).toLocaleString()
                              : 'Never'}
                          </div>
                        </div>
                        {activeKnowledge !== 'current' && (
                          <div className="mt-3 flex items-start gap-2 rounded-lg bg-amber-500/8 p-3 text-[9px] leading-relaxed text-amber-600 dark:text-amber-400">
                            <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
                            Update once after your edit batch and before relying on graph or block context.
                          </div>
                        )}
                      </section>
                      <section className="rounded-xl border bg-card p-5" style={{ borderColor: 'var(--border)' }}>
                        <h2 className="text-sm font-semibold">Maintenance actions</h2>
                        <p className="mt-1 text-[10px] leading-relaxed text-muted-foreground">
                          Normal update batches stale source objects. Rebuild ingests the full PLC source tree.
                        </p>
                        <div className="mt-5 space-y-2">
                          <button className="primary-button w-full" disabled={Boolean(operation)} onClick={() => void updateKnowledge(false)}>
                            <ArrowDownToLine className="h-3.5 w-3.5" /> Update changed components
                          </button>
                          <button className="secondary-button w-full" disabled={Boolean(operation)} onClick={() => void updateKnowledge(true)}>
                            <RefreshCw className="h-3.5 w-3.5" /> Full device rebuild
                          </button>
                        </div>
                        <div className="mt-5 flex items-center gap-2 text-[9px] text-muted-foreground">
                          <ShieldCheck className="h-4 w-4 text-emerald-500" />
                          Applied hashes are checked before stale state clears.
                        </div>
                      </section>
                    </div>

                    {lastImport && (
                      <section className={`rounded-lg border p-4 ${lastImport.importSucceeded ? 'bg-emerald-500/5' : 'bg-red-500/5'}`} style={{ borderColor: 'var(--border)' }}>
                        <div className="flex items-center gap-2 text-[10px] font-medium">
                          {lastImport.importSucceeded ? <CheckCircle2 className="h-4 w-4 text-emerald-500" /> : <AlertCircle className="h-4 w-4 text-red-500" />}
                          Latest import · {lastImport.relativePath}
                        </div>
                        <div className="mt-1 text-[9px] text-muted-foreground">Compile: {lastImport.compileState}. Source retained in this worktree.</div>
                      </section>
                    )}
                  </div>
                )}

                {activeTab === 'chat' && (
                  <div className="h-full min-h-[520px]">
                    <ChatWorkspace
                      tabs={chatTabs}
                      busy={chatBusy}
                      confirmation={pendingConfirmation}
                      onConfirm={decision => void decideConfirmation(decision)}
                      onFocus={sessionId => void activateChatSession(sessionId)}
                      onSend={(sessionId, message) => void sendChatMessage(sessionId, message)}
                      onDraftChange={(sessionId, draft) => setChatTabs(previous => setDraft(previous, sessionId, draft))}
                      onStop={stopChatGeneration}
                      onContinue={sessionId => void continueChat(sessionId)}
                    />
                  </div>
                )}

                {activeTab === 'source' && (
                  <div className="mx-auto max-w-6xl space-y-4 p-5">
                    <section className="rounded-xl border bg-card p-5" style={{ borderColor: 'var(--border)' }}>
                      <div className="flex items-start gap-3">
                        <FileCode2 className="mt-0.5 h-5 w-5 text-chart-3" />
                        <div className="flex-1">
                          <h2 className="text-sm font-semibold">PLC source object</h2>
                          <p className="mt-1 text-[10px] leading-relaxed text-muted-foreground">
                            Enter a device-relative XML path. Preparing validates the source object for editing in this worktree. Import sends the selected source to TIA and retains it afterward.
                          </p>
                        </div>
                      </div>
                      <div className="mt-4 flex gap-2">
                        <input
                          className="field-input flex-1 font-mono"
                          value={relativePath}
                          onChange={event => setRelativePath(event.target.value)}
                          placeholder="Blocks/Main [OB1].xml"
                        />
                        <button className="secondary-button" disabled={!relativePath.trim() || Boolean(operation)} onClick={() => void prepareEdit()}>
                          <Code2 className="h-3.5 w-3.5" /> Prepare source
                        </button>
                        <button className="primary-button" disabled={!relativePath.trim() || Boolean(operation)} onClick={() => void importSource()}>
                          <UploadCloud className="h-3.5 w-3.5" /> Import & compile
                        </button>
                      </div>
                    </section>

                    <section className="overflow-hidden rounded-xl border bg-card" style={{ borderColor: 'var(--border)' }}>
                      <button
                        className="flex w-full items-center gap-2 px-4 py-3 text-left hover:bg-accent/40"
                        onClick={() => setBlockIndexExpanded(previous => !previous)}
                      >
                        {blockIndexExpanded
                          ? <ChevronDown className="h-3.5 w-3.5 text-muted-foreground" />
                          : <ChevronRight className="h-3.5 w-3.5 text-muted-foreground" />}
                        <span className="text-[10px] font-semibold">Persisted PLC block index</span>
                        <span className="ml-auto text-[9px] text-muted-foreground">{blocks.length} blocks</span>
                      </button>
                      {deviceView?.diagnostics.map(diagnostic => (
                        <div
                          key={diagnostic}
                          className="flex items-start gap-2 border-b bg-amber-500/8 px-4 py-2 text-[9px] text-amber-700 dark:text-amber-300"
                          style={{ borderColor: 'var(--border)' }}
                        >
                          <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
                          <span className="break-all">{diagnostic}</span>
                        </div>
                      ))}
                      {blockIndexExpanded && (
                        blocks.length === 0 ? (
                          <div className="p-8 text-center text-[10px] text-muted-foreground">No persisted block index.</div>
                        ) : (
                          <>
                            <div className="border-b px-4 py-2" style={{ borderColor: 'var(--border)' }}>
                              <div className="relative">
                                <Search className="pointer-events-none absolute left-2 top-1/2 h-3 w-3 -translate-y-1/2 text-muted-foreground" />
                                <input
                                  className="field-input w-full pl-7"
                                  value={blockFilter}
                                  onChange={event => setBlockFilter(event.target.value)}
                                  placeholder="Filter by name, path, or type…"
                                />
                              </div>
                            </div>
                            <div className="max-h-[420px] divide-y overflow-y-auto" style={{ borderColor: 'var(--border)' }}>
                              {filteredBlocks.length === 0 ? (
                                <div className="p-8 text-center text-[10px] text-muted-foreground">No blocks match this filter.</div>
                              ) : (
                                filteredBlocks.map(block => (
                                  <button
                                    key={`${block.blockType}:${block.name}:${block.number}`}
                                    className="flex w-full items-center gap-3 px-4 py-2 text-left hover:bg-accent/40"
                                    onClick={() => setRelativePath(block.relativePath)}
                                  >
                                    <FileCode2 className="h-3.5 w-3.5 text-muted-foreground" />
                                    <span className="min-w-0 flex-1 truncate text-[10px]">{block.name}</span>
                                    <span className="font-mono text-[9px] text-muted-foreground">{block.blockType}{block.number}</span>
                                    <span className="text-[9px] text-muted-foreground">{block.programmingLanguage}</span>
                                  </button>
                                ))
                              )}
                            </div>
                          </>
                        )
                      )}
                    </section>
                  </div>
                )}

                {activeTab === 'knowledge' && (
                  <div className="flex h-full min-h-[560px] flex-col p-5">
                    <div className="flex min-h-0 flex-1 flex-col overflow-hidden rounded-xl border bg-card" style={{ borderColor: 'var(--border)' }}>
                      {knowledgeContext && (
                        <NodeEdgesView
                          context={knowledgeContext}
                          projectName={deviceName ?? ''}
                          onNodeSelect={node => setKnowledgeSelection(previous => ({ ...previous, node }))}
                          onEdgeSelect={edge => setKnowledgeSelection(previous => ({ ...previous, edge }))}
                        />
                      )}
                    </div>
                  </div>
                )}

                {activeTab === 'git' && (
                  <div className="h-full min-h-[520px]">
                    <VersionControlPanel
                      workbenchId={selection.workbenchId!}
                      worktreeId={selection.worktreeId!}
                      onSelectionChange={setVersionControlSelection}
                    />
                  </div>
                )}
              </div>
            </>
          )}
        </main>
        {selection.worktreeId && (selection.deviceId !== null || mainView.kind === 'hardware' || activeTab === 'git') && (
          <>
            <div
              role="separator"
              aria-orientation="vertical"
              aria-label="Resize context dock"
              className="dock-resize-handle"
              data-dock-state={shellLayout.rightOpen ? 'open' : 'closed'}
              onPointerDown={event => startDockResize('right', event.clientX)}
            />
            <div
              data-dock="right"
              data-dock-state={shellLayout.rightOpen ? 'open' : 'closed'}
              aria-hidden={!shellLayout.rightOpen}
              className="dock-shell dock-shell-right min-h-0 shrink-0"
              style={{ width: shellLayout.rightOpen ? shellLayout.rightWidth : 0 }}
            >
              {!selection.deviceId && hardwarePage === 'tree' && (
                <HardwarePropertiesDock
                  node={hardwareSelectedNode}
                  tags={hardwareView?.tags ?? []}
                  hidden={false}
                />
              )}
              {selection.deviceId && activeTab === 'overview' && (
                <DevicePropertiesDock
                  meta={deviceMeta}
                  info={deviceInfo}
                  hidden={false}
                />
              )}
              {selection.deviceId && activeTab === 'knowledge' && knowledgeContext && (
                <KnowledgePropertiesDock
                  context={knowledgeContext}
                  node={knowledgeSelection.node}
                  edge={knowledgeSelection.edge}
                  hidden={false}
                />
              )}
              {activeTab === 'git' ? (
                <VersionControlDetailsDock
                  context={{ workbenchId: selection.workbenchId!, worktreeId: selection.worktreeId! }}
                  selection={versionControlSelection}
                  hidden={false}
                />
              ) : selection.deviceId && activeTab !== 'overview' && activeTab !== 'knowledge' && (
                <SessionDock
                  sessions={deviceSessions}
                  activeSessionId={chatTabs.activeId}
                  busy={chatBusy}
                  hidden={false}
                  onCreate={() => void createChatSession()}
                  onActivate={sessionId => void activateChatSession(sessionId)}
                  onRename={(sessionId, title) => void renameChatSession(sessionId, title)}
                  onRemove={sessionId => void removeChatSession(sessionId)}
                  onExport={sessionId => void exportChatSession(sessionId)}
                />
              )}
            </div>
          </>
        )}
        {appAssistantOpen && selection.workbenchId && (
          <AppAssistantPanel
            key={selection.workbenchId}
            workbenchId={selection.workbenchId}
            workbenchName={activeWorkbench?.name ?? 'Selected workbench'}
            runtime={appAssistantRuntime}
            onClose={() => setAppAssistantOpen(false)}
            onSelectWorktree={worktreeId => {
              const worktree = activeWorkbench?.worktrees.find(item => item.worktreeId === worktreeId)
              if (activeWorkbench && worktree) return selectWorktree(activeWorkbench, worktree)
            }}
            onWorkbenchCreated={handleAppAssistantWorkbenchCreated}
          />
        )}
      </div>}

      <footer data-status-bar className="studio-status-bar">
        <span className="studio-status-indicator">● Ready</span>
        <RuntimeStateStatusBar runtime={appAssistantRuntime} />
        <span className="studio-status-context">
          <span>{activeWorkbench?.name ?? 'No workbench'}</span>
          <span>/</span>
          <span className="font-mono">{activeWorktree?.branch ?? 'no worktree'}</span>
          <span>/</span>
          <span className="font-mono text-foreground">{deviceName ?? (selection.worktreeId && !selection.deviceId ? (mainView.kind === 'hardware' ? 'hardware' : 'worktree') : selection.deviceId ?? 'no device')}</span>
        </span>
        <button
          className="studio-status-item studio-status-api"
          data-api-status
          title="Manage DeepSeek API key"
          onClick={() => setApiKeyDialogOpen(true)}
        >
          <CircleDot className={`h-3 w-3 ${fatalError ? 'text-red-500' : apiKeyConfigured === false ? 'text-amber-500' : 'text-emerald-500'}`} />
          {fatalError ? 'API error' : apiKeyConfigured === false ? 'No valid API key' : 'API online'}
        </button>
        {apiKeyConfigured && (
          <span className="studio-status-item" data-api-balance title={apiBalance?.fetchedAt ? `Fetched ${new Date(apiBalance.fetchedAt).toLocaleString()}` : 'DeepSeek account balance'}>
            <span>
              Balance {apiBalance?.balances.map(balance => `${balance.currency === 'USD' ? '$' : `${balance.currency} `}${balance.totalBalance}`).join(' · ') ?? '—'}
            </span>
            <button
              className="icon-button h-4 w-4"
              aria-label="Refresh DeepSeek balance"
              data-api-balance-refresh
              title="Refresh DeepSeek balance"
              disabled={apiBalanceBusy}
              onClick={() => void reloadBalance()}
            >
              <RefreshCw className={`h-3 w-3 ${apiBalanceBusy ? 'animate-spin' : ''}`} />
            </button>
          </span>
        )}
        <span className="studio-status-item">
          <Server className="h-3 w-3 text-chart-3" />
          {sessions.length} TIA session{sessions.length === 1 ? '' : 's'}
        </span>
        <span className="flex-1" />
        <button className="icon-button" aria-label="Refresh status" title="Refresh status" onClick={() => void loadStartup()}>
          <RefreshCw className="h-3 w-3" />
        </button>
        <button className="icon-button" aria-label="Settings" title="Settings" onClick={() => setApiKeyDialogOpen(true)}>
          <Settings2 className="h-3.5 w-3.5" />
        </button>
      </footer>

      {createWorkbenchOpen && (
        <CreateWorkbenchDialog
          sessions={sessions}
          sandboxRoots={sandboxRoots}
          busy={operation === 'create-workbench'}
          operationStatus={activeOperation?.kind === 'create-workbench' ? activeOperation.status : null}
          onDismissOperation={dismissActiveOperation}
          onRefreshSessions={async () => { await reloadSessions() }}
          onBrowseProjectFile={api.browseTiaProjectFile}
          onClose={() => setCreateWorkbenchOpen(false)}
          onCreate={createWorkbench}
        />
      )}
      {sandboxDenial && (
        <SandboxDeniedDialog
          message={sandboxDenial.message}
          roots={sandboxDenial.roots}
          onClose={() => setSandboxDenial(null)}
        />
      )}
      {createWorktreeFor && (
        <NewWorktreeDialog
          workbench={createWorktreeFor}
          busy={operation === 'create-worktree'}
          operationStatus={activeOperation?.kind === 'create-worktree' ? activeOperation.status : null}
          onDismissOperation={dismissActiveOperation}
          onClose={() => setCreateWorktreeFor(null)}
          onCreate={createWorktree}
        />
      )}
      {deleteWorkbenchFor && (
        <DeleteWorkbenchDialog
          workbench={deleteWorkbenchFor}
          busy={operation === 'delete-workbench'}
          onClose={() => setDeleteWorkbenchFor(null)}
          onDelete={() => void deleteWorkbench()}
        />
      )}
      {deleteWorktreeFor && (
        <DeleteWorktreeDialog
          workbench={deleteWorktreeFor.workbench}
          worktree={deleteWorktreeFor.worktree}
          busy={operation === 'delete-worktree'}
          onClose={() => setDeleteWorktreeFor(null)}
          onDelete={() => void deleteWorktree()}
        />
      )}
      {preview && (
        <RefreshDialog
          preview={preview}
          busy={operation === 'apply-refresh'}
          autoCommit={activeWorktree?.branch.toLowerCase() === 'master'}
          onClose={() => setPreview(null)}
          onApply={applyRefresh}
        />
      )}
      {compilePrompt && (
        <CompileApprovalDialog
          prompt={compilePrompt}
          busy={operation === 'stage-refresh' || operation === 'bootstrap-device'}
          onCancel={() => setCompilePrompt(null)}
          onApprove={() => void (compilePrompt.flow === 'rebuild' ? rebuildProject(true) : stageRefresh(true))}
        />
      )}
      {apiKeyDialogOpen && (
        <ApiKeyDialog
          onClose={() => setApiKeyDialogOpen(false)}
          onSave={saveApiKey}
        />
      )}
    </div>
  )
}
