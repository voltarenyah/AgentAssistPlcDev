import { useMemo, useState } from 'react'
import { Boxes, FileCode2, FolderOpen, Loader2, RefreshCw, Server, X } from 'lucide-react'
import type { OperationStatus, SessionInfo } from '@/api/client'
import OperationStatusLine from '@/studio/workbench/OperationStatusLine'

type Props = {
  sessions: SessionInfo[]
  sandboxRoots: string[]
  busy: boolean
  operationStatus: OperationStatus | null
  onDismissOperation: () => void
  onRefreshSessions: () => Promise<void>
  onBrowseProjectFile: () => Promise<string | null>
  onClose: () => void
  onCreate: (values: {
    name: string
    rootPath?: string
    engineeringSessionId?: number
    engineeringProjectPath?: string
  }) => Promise<void>
}

const sanitized = (name: string) =>
  name.trim()
    .split('')
    .map(character => character.charCodeAt(0) < 32 || '<>:"/\\|?*'.includes(character) ? '-' : character)
    .join('')
    .replace(/[. ]+$/g, '') || '<workbench-name>'

const isTiaProjectFile = (path: string) => /\.ap17$/i.test(path.trim())

const normalizePath = (path: string) =>
  path.trim().replaceAll('/', '\\').replace(/\\+$/, '').toLowerCase()

export default function CreateWorkbenchDialog({
  sessions,
  sandboxRoots,
  busy,
  operationStatus,
  onDismissOperation,
  onRefreshSessions,
  onBrowseProjectFile,
  onClose,
  onCreate,
}: Props) {
  const [name, setName] = useState('')
  const [rootPath, setRootPath] = useState('')
  const [mode, setMode] = useState<'session' | 'file'>('session')
  const [sessionId, setSessionId] = useState(sessions[0]?.id?.toString() ?? '')
  const [projectFile, setProjectFile] = useState('')
  const [refreshing, setRefreshing] = useState(false)
  const [browsing, setBrowsing] = useState(false)
  const [browseError, setBrowseError] = useState<string | null>(null)
  const selectedSession = sessions.find(session => session.id.toString() === sessionId)
  const defaultPreview = useMemo(
    () => `%LOCALAPPDATA%\\AutomationWorkbench\\Project\\${sanitized(name)}`,
    [name],
  )
  const trimmedProjectFile = projectFile.trim()
  const projectFileValid = isTiaProjectFile(projectFile)
  const projectFileOutsideSandbox = projectFileValid
    && sandboxRoots.length > 0
    && !sandboxRoots.some(root => normalizePath(trimmedProjectFile).startsWith(normalizePath(root)))
  const valid = Boolean(name.trim()) && (mode === 'session'
    ? Boolean(selectedSession?.projectPath)
    : projectFileValid)

  const refreshSessions = async () => {
    setRefreshing(true)
    try {
      await onRefreshSessions()
    } finally {
      setRefreshing(false)
    }
  }

  const browseProjectFile = async () => {
    setBrowsing(true)
    setBrowseError(null)
    try {
      const selectedPath = await onBrowseProjectFile()
      if (selectedPath) setProjectFile(selectedPath)
    } catch (error) {
      setBrowseError(error instanceof Error ? error.message : 'Could not open the TIA project picker.')
    } finally {
      setBrowsing(false)
    }
  }

  return (
    <div className="fixed inset-0 z-50 grid place-items-center bg-black/55 p-5 backdrop-blur-[2px]">
      <div className="w-full max-w-[620px] overflow-hidden rounded-xl border bg-card shadow-2xl" style={{ borderColor: 'var(--border)' }}>
        <div className="flex items-center gap-3 border-b px-5 py-4" style={{ borderColor: 'var(--border)' }}>
          <div className="grid h-9 w-9 place-items-center rounded-lg bg-chart-2/10">
            <Boxes className="h-4 w-4 text-chart-2" />
          </div>
          <div className="flex-1">
            <h2 className="text-sm font-semibold">Create workbench project</h2>
            <p className="text-[10px] text-muted-foreground">One shared repository, complete linked worktrees, device-owned knowledge.</p>
          </div>
          <button className="icon-button" onClick={onClose} disabled={busy}><X className="h-4 w-4" /></button>
        </div>

        <div className="space-y-4 p-5">
          {busy && (
            <div className="flex items-start gap-2 rounded-lg border border-chart-2/30 bg-chart-2/10 p-3 text-[10px]" data-creation-progress aria-live="polite">
              <Loader2 className="mt-0.5 h-3.5 w-3.5 shrink-0 animate-spin text-chart-2" />
              <div className="min-w-0">
                <div className="font-medium">Creating workbench project…</div>
                <div className="mt-0.5 text-muted-foreground">Preparing the repository, linked worktree, and device context. This may take a little while.</div>
              </div>
            </div>
          )}
          <label className="field-label">
            <span>Workbench name</span>
            <input className="field-input" value={name} onChange={event => setName(event.target.value)} placeholder="Line-7 commissioning" autoFocus />
          </label>

          <div className="field-label">
            <span>TIA project</span>
            <div className="flex gap-1.5">
              <button
                className={`secondary-button flex-1 ${mode === 'session' ? 'border-chart-2 text-foreground' : 'text-muted-foreground'}`}
                onClick={() => setMode('session')}
              >
                <Server className="h-3.5 w-3.5" /> Attach to running TIA
              </button>
              <button
                className={`secondary-button flex-1 ${mode === 'file' ? 'border-chart-2 text-foreground' : 'text-muted-foreground'}`}
                onClick={() => setMode('file')}
              >
                <FileCode2 className="h-3.5 w-3.5" /> Open project file (.ap17)
              </button>
            </div>
          </div>

          {mode === 'session' ? (
            <label className="field-label">
              <span>Running TIA session</span>
              <div className="flex gap-1.5">
                <select className="field-input flex-1" value={sessionId} onChange={event => setSessionId(event.target.value)}>
                  <option value="">Select an open TIA project…</option>
                  {sessions.map(session => (
                    <option key={session.id} value={session.id}>
                      PID {session.id} · {session.projectPath ?? 'No project loaded'}
                    </option>
                  ))}
                </select>
                <button
                  className="icon-button self-center"
                  aria-label="Refresh TIA sessions"
                  title="Refresh TIA sessions"
                  disabled={refreshing || busy}
                  onClick={() => void refreshSessions()}
                >
                  <RefreshCw className={`h-3.5 w-3.5 ${refreshing ? 'animate-spin' : ''}`} />
                </button>
              </div>
              {sessions.length === 0 && (
                <span className="text-[9px] text-amber-500">Open the engineering project in TIA Portal, then refresh sessions.</span>
              )}
            </label>
          ) : (
            <label className="field-label">
              <span>TIA project file</span>
              <div className="relative">
                <FileCode2 className="absolute left-3 top-2.5 h-3.5 w-3.5 text-muted-foreground" />
                <div className="flex gap-1.5">
                  <input
                    className="field-input min-w-0 flex-1 pl-9 font-mono"
                    value={projectFile}
                    onChange={event => setProjectFile(event.target.value)}
                    placeholder="C:\\Users\\…\\Documents\\Automation\\Line\\Line.ap17"
                  />
                  <button
                    className="secondary-button shrink-0"
                    type="button"
                    aria-label="Browse for TIA project file"
                    onClick={() => void browseProjectFile()}
                    disabled={browsing || busy}
                  >
                    {browsing ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <FolderOpen className="h-3.5 w-3.5" />}
                    Browse
                  </button>
                </div>
              </div>
              <span className="text-[9px] text-muted-foreground">A new TIA Portal instance is launched with this project open.</span>
              {browseError && <span className="text-[9px] text-amber-500">{browseError}</span>}
              {trimmedProjectFile && !projectFileValid && (
                <span className="text-[9px] text-amber-500">Enter the full path to a TIA Portal project file (.ap17).</span>
              )}
              {projectFileOutsideSandbox && (
                <span className="text-[9px] text-amber-500">This path is outside the sandbox whitelist. Move the project under an allowed root, or creation will be denied.</span>
              )}
            </label>
          )}

          <label className="field-label">
            <span>Custom root <em className="font-normal text-muted-foreground">optional</em></span>
            <div className="relative">
              <FolderOpen className="absolute left-3 top-2.5 h-3.5 w-3.5 text-muted-foreground" />
              <input
                className="field-input pl-9"
                value={rootPath}
                onChange={event => setRootPath(event.target.value)}
                placeholder="D:\\Automation\\MyWorkbench"
              />
            </div>
          </label>

          <div className="rounded-lg border bg-muted/40 p-3" style={{ borderColor: 'var(--border)' }}>
            <div className="text-[9px] uppercase tracking-[0.16em] text-muted-foreground">Resolved location</div>
            <div className="mt-1 break-all font-mono text-[10px]">{rootPath.trim() || defaultPreview}</div>
          </div>
        </div>

        <div className="flex items-center justify-between border-t bg-muted/25 px-5 py-3" style={{ borderColor: 'var(--border)' }}>
          <OperationStatusLine
            status={operationStatus}
            fallback={busy ? 'Preparing workbench storage...' : undefined}
            onDismiss={onDismissOperation}
          />
          <div className="flex gap-2">
            <button className="secondary-button" onClick={onClose} disabled={busy}>Cancel</button>
            <button
              className="primary-button"
              disabled={!valid || busy}
              onClick={() => onCreate(mode === 'session'
                ? {
                    name: name.trim(),
                    rootPath: rootPath.trim() || undefined,
                    engineeringSessionId: Number(sessionId),
                  }
                : {
                    name: name.trim(),
                    rootPath: rootPath.trim() || undefined,
                    engineeringProjectPath: trimmedProjectFile,
                  })}
            >
              {busy && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
              Create workbench
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}
