import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  AlertCircle,
  ArrowDownToLine,
  Boxes,
  CheckCircle2,
  CircleDot,
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
  PanelRightClose,
  PanelRightOpen,
  Plus,
  RefreshCw,
  RotateCw,
  Server,
  ShieldCheck,
  Sparkles,
  Trash2,
  UploadCloud,
  X,
} from 'lucide-react'
import { toast } from 'sonner'
import { ThemeToggle } from '@/catalog/ThemeToggle'
import GitPanel from '@/studio/panels/GitPanel'
import WorkbenchNavigator, {
  type WorkbenchSelection,
} from '@/studio/workbench/WorkbenchNavigator'
import CreateWorkbenchDialog from '@/studio/workbench/CreateWorkbenchDialog'
import OperationStatusLine from '@/studio/workbench/OperationStatusLine'
import RefreshDialog from '@/studio/workbench/RefreshDialog'
import SandboxDeniedDialog from '@/studio/workbench/SandboxDeniedDialog'
import {
  applyDeviceSnapshot,
  beginDeviceSelection,
  completeDeviceSelection,
  failDeviceSelection,
  retainSnapshotOnError,
  type DeviceSelectionState,
} from '@/studio/deviceSnapshot'
import * as api from '@/api/client'
import { runOpenProjectInTia } from '@/studio/deviceActions'
import ChatWorkspace from '@/studio/chat/ChatWorkspace'
import SessionDock from '@/studio/chat/SessionDock'
import {
  appendAssistantDelta,
  appendLocalUserMessage,
  appendProgressMessage,
  closeTab,
  emptyChatTabs,
  openTab,
  renameTab,
  setTurnMeta,
  type ChatTabsState,
} from '@/studio/chat/chatTabState'

type StudioTab = 'overview' | 'chat' | 'source' | 'knowledge' | 'git'
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

function NewWorktreeDialog({
  workbench,
  busy,
  onClose,
  onCreate,
}: {
  workbench: api.Workbench
  busy: boolean
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
          <button className="icon-button" onClick={onClose}><X className="h-4 w-4" /></button>
        </div>
        <div className="space-y-4 p-5">
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
        <div className="flex justify-end gap-2 border-t bg-muted/25 px-5 py-3" style={{ borderColor: 'var(--border)' }}>
          <button className="secondary-button" onClick={onClose}>Cancel</button>
          <button className="primary-button" disabled={!valid || busy} onClick={() => onCreate(name.trim(), branch.trim(), startPoint.trim() || undefined)}>
            {busy && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
            Create worktree
          </button>
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
            This permanently deletes the workbench directory — all linked worktrees, the shared Git repository with its full history, exported baselines, knowledge databases, and saved chat sessions.
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
  const [devicesByWorktree, setDevicesByWorktree] = useState<Record<string, string[]>>({})
  const [selection, setSelection] = useState<WorkbenchSelection>({
    workbenchId: null,
    worktreeId: null,
    deviceId: null,
  })
  const [deviceSelection, setDeviceSelection] = useState<DeviceSelectionState | null>(null)
  const selectionRequestId = useRef(0)
  const [activeTab, setActiveTab] = useState<StudioTab>('overview')
  const [chatTabs, setChatTabs] = useState<ChatTabsState>(() => emptyChatTabs())
  const [sessionDockVisible, setSessionDockVisible] = useState(true)
  const [chatBusy, setChatBusy] = useState(false)
  const [loading, setLoading] = useState(true)
  const [operation, setOperation] = useState<string | null>(null)
  const [activeOperation, setActiveOperation] = useState<ActiveOperation | null>(null)
  const [fatalError, setFatalError] = useState<string | null>(null)
  const [createWorkbenchOpen, setCreateWorkbenchOpen] = useState(false)
  const [sandboxRoots, setSandboxRoots] = useState<string[]>([])
  const [sandboxDenial, setSandboxDenial] = useState<{ message: string; roots: string[] } | null>(null)
  const [createWorktreeFor, setCreateWorktreeFor] = useState<api.Workbench | null>(null)
  const [deleteWorkbenchFor, setDeleteWorkbenchFor] = useState<api.Workbench | null>(null)
  const [preview, setPreview] = useState<api.ReconciliationPreview | null>(null)
  const [compilePrompt, setCompilePrompt] = useState<CompilePrompt | null>(null)
  const [apiKeyConfigured, setApiKeyConfigured] = useState<boolean | null>(null)
  const [apiKeyDialogOpen, setApiKeyDialogOpen] = useState(false)
  const [relativePath, setRelativePath] = useState('')
  const [lastImport, setLastImport] = useState<api.ImportModifiedResult | null>(null)

  const activeWorkbench = useMemo(
    () => workbenches.find(workbench => workbench.workbenchId === selection.workbenchId) ?? null,
    [selection.workbenchId, workbenches],
  )
  const activeWorktree = useMemo(
    () => activeWorkbench?.worktrees.find(worktree => worktree.worktreeId === selection.worktreeId) ?? null,
    [activeWorkbench, selection.worktreeId],
  )
  const deviceView = deviceSelection?.view ?? null
  const deviceSessions = deviceSelection?.sessions ?? []
  const deviceInfo = deviceView?.snapshot ?? null
  const deviceMeta = deviceInfo?.device ?? null
  const blocks = deviceView?.blocks ?? []
  const touchedCount = deviceView?.overlayCount ?? 0
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
    } catch {
      setApiKeyConfigured(null)
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
        setSelection({ workbenchId: first.workbenchId, worktreeId: null, deviceId: null })
      }
    } catch (error) {
      setFatalError(displayError(error))
    } finally {
      setLoading(false)
    }
    void reloadKeyStatus()
  }, [reloadWorkbenches, reloadSessions, reloadKeyStatus])

  useEffect(() => { void loadStartup() }, [loadStartup])

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
      toast.error(`Offline device state could not be refreshed: ${displayError(error)}`)
      return null
    }
  }, [])

  const selectWorkbench = async (workbench: api.Workbench) => {
    try {
      setSelection({ workbenchId: workbench.workbenchId, worktreeId: null, deviceId: null })
      setDeviceSelection(null)
      setChatTabs(emptyChatTabs())
    } catch (error) {
      toast.error(displayError(error))
    }
  }

  const selectWorktree = async (workbench: api.Workbench, worktree: api.WorkbenchRegistration) => {
    setOperation('select-worktree')
    try {
      const devices = await api.listDevices(workbench.workbenchId, worktree.worktreeId)
      setDevicesByWorktree(previous => ({
        ...previous,
        [worktreeKey(workbench.workbenchId, worktree.worktreeId)]: devices,
      }))
      setSelection({ workbenchId: workbench.workbenchId, worktreeId: worktree.worktreeId, deviceId: null })
      setDeviceSelection(null)
      setChatTabs(emptyChatTabs())
      if (devices.length === 1) {
        await selectDevice(workbench, worktree, devices[0])
      }
    } catch (error) {
      toast.error(displayError(error))
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
    setDeviceSelection(previous => beginDeviceSelection(previous, deviceId, requestId))
    setChatTabs(emptyChatTabs())
    setOperation('select-device')
    try {
      const [snapshot, savedSessions] = await Promise.all([
        api.getDeviceInfo(workbench.workbenchId, worktree.worktreeId, deviceId),
        api.listDeviceSessions(workbench.workbenchId, worktree.worktreeId, deviceId).catch(() => []),
      ])
      if (selectionRequestId.current !== requestId) return
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
      toast.error(displayError(error))
    } finally {
      if (selectionRequestId.current === requestId) setOperation(null)
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
        toast.error(displayError(error))
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
      toast.error(displayError(error))
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
      toast.error(displayError(error))
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
      toast.error(displayError(error))
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
      toast.error(displayError(error))
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
      toast.error(displayError(error))
    } finally {
      setChatBusy(false)
    }
  }

  const sendChatMessage = async (sessionId: string, message: string) => {
    setChatBusy(true)
    setChatTabs(previous => appendLocalUserMessage(previous, sessionId, message))
    try {
      await ensureChatContext()
      if (chatTabs.activeId !== sessionId) await api.loadChatSession(sessionId)
      await api.sendChatMessage(message, event => {
        if (event.kind === 'progress') {
          setChatTabs(previous => appendProgressMessage(previous, sessionId, event.delta))
        } else if (event.kind === 'content' || event.kind === 'reasoning') {
          setChatTabs(previous => appendAssistantDelta(previous, sessionId, event.delta))
        } else if (event.kind === 'error') {
          setChatTabs(previous => appendProgressMessage(previous, sessionId, `Error: ${event.delta}`))
        } else if (event.kind === 'meta') {
          setChatTabs(previous => setTurnMeta(previous, sessionId, event.usage ?? null, event.hitRoundCap ?? false))
        }
      })
      const session = await api.loadChatSession(sessionId)
      setChatTabs(previous => openTab(previous, session))
      await refreshChatSessions()
    } catch (error) {
      toast.error(displayError(error))
    } finally {
      setChatBusy(false)
    }
  }

  const continueChat = async (sessionId: string) => {
    setChatBusy(true)
    try {
      await ensureChatContext()
      if (chatTabs.activeId !== sessionId) await api.loadChatSession(sessionId)
      await api.grantChatRounds(6)
    } catch (error) {
      toast.error(displayError(error))
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
      toast.error(displayError(error))
    } finally {
      setOperation(null)
    }
  }

  const stageRefresh = async (allowCompile = false) => {
    if (!selection.workbenchId || !selection.worktreeId || !selection.deviceId) return
    const context = { workbenchId: selection.workbenchId, worktreeId: selection.worktreeId, deviceId: selection.deviceId }
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
        toast.error(displayError(error))
      }
    } finally {
      setOperation(null)
    }
  }

  const openProjectInTia = async () => {
    if (!activeWorkbench || !activeWorktree || !selection.deviceId) return
    const context = {
      workbenchId: activeWorkbench.workbenchId,
      worktreeId: activeWorktree.worktreeId,
      deviceId: selection.deviceId,
    }
    setOperation('open-tia-project')
    const op = beginOperation('open-tia-project', 'Opening registered project in TIA Portal...')
    try {
      await runOpenProjectInTia(api.openDeviceProject, { ...context, operationId: op.id })
      toast.success('Registered project opened in TIA Portal')
    } catch (error) {
      // A live session query is only warranted now: if the project is already
      // open in a running TIA instance, surface the re-attach option instead of
      // leaving the user with a bare "another user has it open" error.
      const live = await api.getSessions().catch(() => null)
      if (live) setSessions(live)
      const source = deviceInfo?.sourceProjectPath
      const match = source
        ? live?.find(session => session.projectPath
            && normalizeProjectPath(session.projectPath) === normalizeProjectPath(source))
        : null
      if (match) {
        toast.error(`Project is already open in TIA (PID ${match.id}) — use Re-attach TIA instance instead.`)
      } else {
        toast.error(displayError(error))
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
      toast.error(displayError(error))
    } finally {
      setOperation(null)
    }
  }

  const applyRefresh = async (approvedPaths: string[]) => {
    if (!selection.workbenchId || !selection.worktreeId || !selection.deviceId || !preview) return
    const context = { workbenchId: selection.workbenchId, worktreeId: selection.worktreeId, deviceId: selection.deviceId }
    setOperation('apply-refresh')
    const op = beginOperation('apply-refresh', 'Applying approved refresh...')
    try {
      const result = await api.applyDeviceRefresh(context.workbenchId, context.worktreeId, context.deviceId, preview.previewId, approvedPaths, op.id)
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
        toast.error('The preview is stale. Stage and review the export again.')
      } else {
        toast.error(message)
      }
    } finally {
      setOperation(null)
    }
  }

  const updateKnowledge = async (rebuild = false) => {
    if (!selection.workbenchId || !selection.worktreeId || !selection.deviceId) return
    const context = { workbenchId: selection.workbenchId, worktreeId: selection.worktreeId, deviceId: selection.deviceId }
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
      toast.error(displayError(error))
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
      toast.success('Sparse overlay prepared. Edit the modified-source copy.')
    } catch (error) {
      toast.error(displayError(error))
    } finally {
      setOperation(null)
    }
  }

  const importSource = async () => {
    if (!selection.workbenchId || !selection.worktreeId || !selection.deviceId || !relativePath.trim()) return
    const context = { workbenchId: selection.workbenchId, worktreeId: selection.worktreeId, deviceId: selection.deviceId }
    setOperation('import-source')
    const op = beginOperation('import-source', 'Importing modified source...')
    try {
      const result = await api.importDeviceSource(context.workbenchId, context.worktreeId, context.deviceId, relativePath.trim(), op.id)
      setLastImport(result)
      await reloadDeviceSnapshot(context)
      if (result.importSucceeded && result.compileState.toLowerCase().includes('success')) {
        toast.success('Overlay imported and compiled; modified file retained')
      } else {
        toast.warning(result.error || `Compile state: ${result.compileState}`)
      }
    } catch (error) {
      toast.error(displayError(error))
    } finally {
      setOperation(null)
    }
  }

  const mergeIntoMaster = async () => {
    if (!activeWorkbench || !activeWorktree || activeWorktree.branch === 'master') return
    const target = activeWorkbench.worktrees.find(worktree => worktree.branch === 'master')
    if (!target) return
    setOperation('merge-worktree')
    const op = beginOperation('merge-worktree', 'Merging worktree...')
    try {
      await api.mergeWorktree(activeWorkbench.workbenchId, activeWorktree.worktreeId, target.worktreeId, op.id)
      toast.success(`${activeWorktree.branch} merged into master`)
      await reloadWorkbenches()
    } catch (error) {
      toast.error(displayError(error))
    } finally {
      setOperation(null)
    }
  }

  const saveApiKey = async (apiKey: string) => {
    await api.saveApiKey(apiKey)
    await reloadKeyStatus()
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
        setDeviceSelection(null)
        setChatTabs(emptyChatTabs())
      }
      await reloadWorkbenches()
      toast.success(`Workbench “${workbench.name}” deleted`)
    } catch (error) {
      toast.error(displayError(error))
    } finally {
      setOperation(null)
    }
  }

  const bootstrapDevice = async () => {
    if (!selection.workbenchId || !selection.worktreeId || !selection.deviceId) return
    const context = { workbenchId: selection.workbenchId, worktreeId: selection.worktreeId, deviceId: selection.deviceId }
    setOperation('bootstrap-device')
    const op = beginOperation('bootstrap-device', 'Generating PLC context: export, baseline commit, knowledge ingest...')
    try {
      await api.bootstrapDevice(context.workbenchId, context.worktreeId, context.deviceId, op.id)
      await reloadDeviceSnapshot(context)
      setActiveTab('chat')
      toast.success('PLC context ready — start chatting to explore your project.')
    } catch (error) {
      toast.error(displayError(error))
    } finally {
      setOperation(null)
    }
  }

  const [rebuildArmed, setRebuildArmed] = useState(false)

  const rebuildProject = async (allowCompile = false) => {
    if (!selection.workbenchId || !selection.worktreeId || !selection.deviceId) return
    const context = { workbenchId: selection.workbenchId, worktreeId: selection.worktreeId, deviceId: selection.deviceId }
    setCompilePrompt(null)
    setOperation('bootstrap-device')
    const op = beginOperation(
      'bootstrap-device',
      allowCompile
        ? 'Compiling selected PLC and retrying rebuild...'
        : 'Rebuilding project: full export, baseline commit, knowledge ingest...',
    )
    try {
      await api.bootstrapDevice(context.workbenchId, context.worktreeId, context.deviceId, op.id, 'rebuild: full export', allowCompile)
      await reloadDeviceSnapshot(context)
      toast.success('Project rebuilt from TIA — baseline and knowledge refreshed.')
    } catch (error) {
      if (!allowCompile
        && error instanceof api.WorkbenchApiError
        && error.code === 'PLC_COMPILE_REQUIRED') {
        setCompilePrompt({ message: error.message, flow: 'rebuild', context })
      } else {
        toast.error(displayError(error))
      }
    } finally {
      setOperation(null)
    }
  }

  const tabs: Array<{ id: StudioTab; label: string; icon: typeof Boxes }> = [
    { id: 'overview', label: 'Device overview', icon: Cpu },
    { id: 'chat', label: 'AI chat', icon: MessageSquare },
    { id: 'source', label: 'Source overlays', icon: Code2 },
    { id: 'knowledge', label: 'Knowledge', icon: Database },
    { id: 'git', label: 'Git worktree', icon: GitBranch },
  ]

  return (
    <div className="flex h-screen min-h-[620px] flex-col overflow-hidden bg-background text-foreground">
      <header className="flex h-12 shrink-0 items-center border-b bg-card px-3" style={{ borderColor: 'var(--border)' }}>
        <div className="flex items-center gap-2">
          <div className="grid h-7 w-7 place-items-center rounded-md bg-chart-2 text-white">
            <CloudCog className="h-4 w-4" />
          </div>
          <div>
            <div className="text-[11px] font-semibold tracking-wide">PLC ENGINEERING STUDIO</div>
            <div className="text-[8px] uppercase tracking-[0.16em] text-muted-foreground">Workbench lifecycle console</div>
          </div>
        </div>
        <div className="mx-5 h-5 w-px bg-border" />
        <div className="flex min-w-0 flex-1 items-center gap-1.5 text-[9px] text-muted-foreground">
          <span className="truncate">{activeWorkbench?.name ?? 'No workbench'}</span>
          <span>/</span>
          <span className="truncate font-mono">{activeWorktree?.branch ?? 'no worktree'}</span>
          <span>/</span>
          <span className="truncate font-mono text-foreground">{deviceInfo?.plcName ?? selection.deviceId ?? 'no device'}</span>
        </div>
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
            className="flex items-center gap-1.5 rounded-full border px-2 py-1 text-[9px] transition-colors hover:bg-accent/50"
            style={{ borderColor: 'var(--border)' }}
            data-api-status
            title="Manage DeepSeek API key"
            onClick={() => setApiKeyDialogOpen(true)}
          >
            <CircleDot className={`h-3 w-3 ${fatalError ? 'text-red-500' : apiKeyConfigured === false ? 'text-amber-500' : 'text-emerald-500'}`} />
            {fatalError ? 'API error' : apiKeyConfigured === false ? 'No valid API key' : 'API online'}
          </button>
          <div className="flex items-center gap-1.5 rounded-full border px-2 py-1 text-[9px]" style={{ borderColor: 'var(--border)' }}>
            <Server className="h-3 w-3 text-chart-3" />
            {sessions.length} TIA session{sessions.length === 1 ? '' : 's'}
          </div>
          {selection.deviceId && (
            <button
              className="icon-button"
              aria-label={sessionDockVisible ? 'Hide AI sessions' : 'Show AI sessions'}
              onClick={() => setSessionDockVisible(value => !value)}
            >
              {sessionDockVisible ? <PanelRightClose className="h-3.5 w-3.5" /> : <PanelRightOpen className="h-3.5 w-3.5" />}
            </button>
          )}
          <ThemeToggle />
        </div>
      </header>

      <div className="flex min-h-0 flex-1">
        <WorkbenchNavigator
          workbenches={workbenches}
          devicesByWorktree={devicesByWorktree}
          selection={selection}
          knowledgeState={navigatorKnowledgeState}
          loading={loading}
          onCreateWorkbench={openCreateWorkbench}
          onCreateWorktree={setCreateWorktreeFor}
          onRefresh={() => void loadStartup()}
          onSelectWorkbench={workbench => void selectWorkbench(workbench)}
          onSelectWorktree={(workbench, worktree) => void selectWorktree(workbench, worktree)}
          onSelectDevice={(workbench, worktree, deviceId) => void selectDevice(workbench, worktree, deviceId)}
          onDeleteWorkbench={workbench => setDeleteWorkbenchFor(workbench)}
        />

        <main className="flex min-w-0 flex-1 flex-col">
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
                        <h1 className="text-lg font-semibold">{deviceInfo?.plcName ?? selection.deviceId}</h1>
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
                      <Metric label="Touched overlays" value={touchedCount} tone={touchedCount ? 'warning' : 'neutral'} />
                      <Metric label="Saved sessions" value={deviceSessions.length} />
                      <Metric label="Knowledge state" value={activeKnowledge} tone={activeKnowledge === 'current' ? 'good' : activeKnowledge === 'failed' ? 'danger' : 'warning'} />
                    </div>

                    {deviceMeta && (
                      <section className="rounded-xl border bg-card p-5" style={{ borderColor: 'var(--border)' }}>
                        <div className="flex items-center gap-3">
                          <Boxes className="h-5 w-5 text-chart-2" />
                          <div>
                            <h2 className="text-sm font-semibold">TIA project</h2>
                            <p className="text-[9px] text-muted-foreground">Captured at last export · refreshes on the next export or sync</p>
                          </div>
                        </div>
                        <div className="mt-4 grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
                          {([
                            ['Project', deviceMeta.projectName],
                            ['Author', deviceMeta.projectAuthor],
                            ['Version', deviceMeta.projectVersion],
                            ['Copyright', deviceMeta.projectCopyright],
                            ['Created', deviceMeta.projectCreationTime ? new Date(deviceMeta.projectCreationTime).toLocaleString() : null],
                            ['Last modified', deviceMeta.projectLastModified ? new Date(deviceMeta.projectLastModified).toLocaleString() : null],
                            ['Modified by', deviceMeta.projectLastModifiedBy],
                            ['PLC type', deviceMeta.typeIdentifier?.replace(/^OrderNumber:/, '') ?? null],
                          ] as [string, string | null][]).filter(([, value]) => value).map(([label, value]) => (
                            <div key={label} className="rounded-lg border bg-muted/30 p-3" style={{ borderColor: 'var(--border)' }}>
                              <div className="text-[8px] uppercase tracking-[0.15em] text-muted-foreground">{label}</div>
                              <div className="mt-1.5 break-all text-[10px] leading-relaxed">{value}</div>
                            </div>
                          ))}
                        </div>
                        {deviceMeta.projectComment && (
                          <div className="mt-3 rounded-lg border bg-muted/30 p-3" style={{ borderColor: 'var(--border)' }}>
                            <div className="text-[8px] uppercase tracking-[0.15em] text-muted-foreground">Project comment</div>
                            <div className="mt-1.5 whitespace-pre-wrap text-[10px] leading-relaxed">{deviceMeta.projectComment}</div>
                          </div>
                        )}
                      </section>
                    )}

                    <section className="grid gap-3 lg:grid-cols-3">
                      {[
                        ['Exported baseline', deviceInfo?.exportedSourceRoot],
                        ['Modified overlay', deviceInfo?.modifiedSourceRoot],
                        ['Device knowledge DB', deviceInfo?.knowledgeDbPath],
                      ].map(([label, value]) => (
                        <div key={label} className="rounded-lg border bg-card p-4" style={{ borderColor: 'var(--border)' }}>
                          <div className="text-[8px] uppercase tracking-[0.15em] text-muted-foreground">{label}</div>
                          <div className="mt-2 break-all font-mono text-[9px] leading-relaxed">{value ?? 'Loading…'}</div>
                        </div>
                      ))}
                    </section>

                    {lastImport && (
                      <section className={`rounded-lg border p-4 ${lastImport.importSucceeded ? 'bg-emerald-500/5' : 'bg-red-500/5'}`} style={{ borderColor: 'var(--border)' }}>
                        <div className="flex items-center gap-2 text-[10px] font-medium">
                          {lastImport.importSucceeded ? <CheckCircle2 className="h-4 w-4 text-emerald-500" /> : <AlertCircle className="h-4 w-4 text-red-500" />}
                          Latest import · {lastImport.relativePath}
                        </div>
                        <div className="mt-1 text-[9px] text-muted-foreground">Compile: {lastImport.compileState}. Overlay retained in this worktree.</div>
                      </section>
                    )}
                  </div>
                )}

                {activeTab === 'chat' && (
                  <div className="h-full min-h-[520px]">
                    <ChatWorkspace
                      tabs={chatTabs}
                      busy={chatBusy}
                      onFocus={sessionId => void activateChatSession(sessionId)}
                      onSend={(sessionId, message) => void sendChatMessage(sessionId, message)}
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
                          <h2 className="text-sm font-semibold">Sparse modified-source overlay</h2>
                          <p className="mt-1 text-[10px] leading-relaxed text-muted-foreground">
                            Enter a device-relative XML path. Preparing copies the effective baseline only once. Import sends only the overlay back to TIA and retains it afterward.
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
                          <Code2 className="h-3.5 w-3.5" /> Prepare overlay
                        </button>
                        <button className="primary-button" disabled={!relativePath.trim() || Boolean(operation)} onClick={() => void importSource()}>
                          <UploadCloud className="h-3.5 w-3.5" /> Import & compile
                        </button>
                      </div>
                    </section>

                    <section className="overflow-hidden rounded-xl border bg-card" style={{ borderColor: 'var(--border)' }}>
                      <div className="flex items-center border-b px-4 py-3" style={{ borderColor: 'var(--border)' }}>
                        <span className="text-[10px] font-semibold">Persisted PLC block index</span>
                        <span className="ml-auto text-[9px] text-muted-foreground">{blocks.length} blocks</span>
                      </div>
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
                      {blocks.length === 0 ? (
                        <div className="p-8 text-center text-[10px] text-muted-foreground">No persisted block index.</div>
                      ) : (
                        <div className="divide-y" style={{ borderColor: 'var(--border)' }}>
                          {blocks.map(block => (
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
                          ))}
                        </div>
                      )}
                    </section>
                  </div>
                )}

                {activeTab === 'knowledge' && (
                  <div className="mx-auto grid max-w-5xl gap-4 p-5 lg:grid-cols-2">
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
                        Normal update batches all stale overlays. Rebuild ingests the full exported baseline plus sparse overlay.
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
                )}

                {activeTab === 'git' && (
                  <div className="h-full min-h-[520px]">
                    <GitPanel
                      workbenchId={selection.workbenchId!}
                      worktreeId={selection.worktreeId!}
                      deviceId={selection.deviceId}
                    />
                  </div>
                )}
              </div>
            </>
          )}
        </main>
        {selection.deviceId && (
          <SessionDock
            sessions={deviceSessions}
            activeSessionId={chatTabs.activeId}
            busy={chatBusy}
            hidden={!sessionDockVisible}
            onCreate={() => void createChatSession()}
            onActivate={sessionId => void activateChatSession(sessionId)}
            onRename={(sessionId, title) => void renameChatSession(sessionId, title)}
            onRemove={sessionId => void removeChatSession(sessionId)}
            onExport={sessionId => void exportChatSession(sessionId)}
          />
        )}
      </div>

      {createWorkbenchOpen && (
        <CreateWorkbenchDialog
          sessions={sessions}
          sandboxRoots={sandboxRoots}
          busy={operation === 'create-workbench'}
          operationStatus={activeOperation?.kind === 'create-workbench' ? activeOperation.status : null}
          onDismissOperation={dismissActiveOperation}
          onRefreshSessions={async () => { await reloadSessions() }}
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
      {preview && (
        <RefreshDialog
          preview={preview}
          busy={operation === 'apply-refresh'}
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
