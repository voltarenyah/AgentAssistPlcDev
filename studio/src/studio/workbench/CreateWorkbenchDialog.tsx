import { useMemo, useState } from 'react'
import { FileCode2, FolderOpen, FolderPlus, Loader2, RefreshCw, Server, X } from 'lucide-react'
import type { OperationStatus, SessionInfo } from '@/api/client'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import OperationStatusLine from '@/studio/workbench/OperationStatusLine'
import OperationTimingList from '@/studio/workbench/OperationTimingList'

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

  if (busy) {
    return (
      <Dialog open onOpenChange={() => undefined}>
        <DialogContent
          showCloseButton={false}
          className="flex h-[calc(100vh-2.5rem)] max-h-[44rem] max-w-5xl flex-col gap-0 overflow-hidden p-0"
          data-creation-progress
          aria-live="polite"
          onEscapeKeyDown={event => event.preventDefault()}
          onPointerDownOutside={event => event.preventDefault()}
        >
          <DialogHeader className="flex-row items-center gap-3 border-b px-5 py-4 text-left">
            <div className="grid h-9 w-9 place-items-center rounded-lg bg-chart-2/10">
              <Loader2 className="h-4 w-4 animate-spin text-chart-2" aria-hidden="true" />
            </div>
            <div>
              <DialogTitle className="text-sm">Creating workbench project…</DialogTitle>
              <DialogDescription className="text-xs leading-4">Repository, worktree, and TIA source evidence are being prepared.</DialogDescription>
            </div>
          </DialogHeader>
          <div className="min-h-0 flex-1 overflow-hidden p-5">
            <OperationTimingList status={operationStatus} layout="dashboard" className="h-full" />
          </div>
          <div className="border-t bg-muted/25 px-5 py-3 text-xs leading-4 text-muted-foreground">
            The active phase and elapsed time refresh automatically while setup continues.
          </div>
        </DialogContent>
      </Dialog>
    )
  }

  return (
    <Dialog open onOpenChange={open => { if (!open) onClose() }}>
      <DialogContent
        showCloseButton={false}
        data-create-workbench-dialog
        className="flex max-h-[85vh] max-w-[min(620px,calc(100%-2rem))] flex-col gap-0 overflow-hidden p-0"
      >
        <DialogHeader className="flex-row items-center gap-3 border-b px-5 py-4 text-left">
          <div className="grid h-9 w-9 place-items-center rounded-lg bg-chart-2/10">
            <FolderPlus className="h-4 w-4 text-chart-2" />
          </div>
          <div className="flex-1">
            <DialogTitle className="text-sm">Create workbench project</DialogTitle>
            <DialogDescription className="text-xs leading-4">One shared repository, complete linked worktrees, device-owned knowledge.</DialogDescription>
          </div>
          <Button variant="ghost" size="icon-xs" onClick={onClose} disabled={busy} aria-label="Close create workbench dialog"><X /></Button>
        </DialogHeader>

        <div data-create-workbench-form className="scrollbar-sleek min-h-0 flex-1 space-y-4 overflow-y-auto p-5">
          <div className="space-y-1.5">
            <Label htmlFor="workbench-name" className="text-xs">Workbench name</Label>
            <Input id="workbench-name" className="h-8 text-xs" value={name} onChange={event => setName(event.target.value)} placeholder="Line-7 commissioning" autoFocus />
          </div>

          <div className="space-y-1.5">
            <Label className="text-xs">TIA project</Label>
            <div className="relative w-full rounded-md border border-border bg-muted/40 p-0.5">
              <span
                aria-hidden="true"
                className={`pointer-events-none absolute inset-y-0.5 left-0.5 w-1/2 rounded-sm bg-background shadow-sm transition-transform duration-200 ease-out ${mode === 'file' ? 'translate-x-full' : 'translate-x-0'}`}
              />
              <ToggleGroup
                type="single"
                value={mode}
                variant="default"
                size="sm"
                spacing={0}
                className="relative z-10 w-full gap-0"
                onValueChange={value => {
                  if (value === 'session' || value === 'file') setMode(value)
                }}
              >
                <ToggleGroupItem value="session" className="min-w-0 flex-1 rounded-sm bg-transparent text-center text-xs whitespace-normal leading-4 data-[state=on]:bg-transparent">
                  <Server /> Attach to running TIA
                </ToggleGroupItem>
                <ToggleGroupItem value="file" className="min-w-0 flex-1 rounded-sm bg-transparent text-center text-xs whitespace-normal leading-4 data-[state=on]:bg-transparent">
                  <FileCode2 /> Open project file (.ap17)
                </ToggleGroupItem>
              </ToggleGroup>
            </div>
          </div>

          {mode === 'session' ? (
            <div className="space-y-1.5">
              <Label className="text-xs">Running TIA session</Label>
              <div className="flex gap-1.5">
                <Select value={sessionId || undefined} onValueChange={setSessionId}>
                  <SelectTrigger size="sm" className="min-w-0 flex-1 text-xs">
                    <SelectValue placeholder="Select an open TIA project…" />
                  </SelectTrigger>
                  <SelectContent>
                    {sessions.map(session => (
                      <SelectItem key={session.id} value={session.id.toString()}>
                        PID {session.id} · {session.projectPath ?? 'No project loaded'}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                  <Button
                    variant="outline"
                    size="sm"
                    className="h-8 w-[88px] self-center justify-center text-xs"
                    aria-label="Update TIA sessions"
                    title="Update TIA sessions"
                  disabled={refreshing || busy}
                  onClick={() => void refreshSessions()}
                >
                  <RefreshCw className={`h-3.5 w-3.5 ${refreshing ? 'animate-spin' : ''}`} />
                  Update
                </Button>
              </div>
              {sessions.length === 0 && (
                <span className="text-xs leading-4 text-amber-500">Open the engineering project in TIA Portal, then refresh sessions.</span>
              )}
            </div>
          ) : (
            <div className="space-y-1.5">
              <Label htmlFor="tia-project-file" className="text-xs">TIA project file</Label>
              <div className="relative">
                <FileCode2 className="absolute left-3 top-2.5 h-3.5 w-3.5 text-muted-foreground" />
                <div className="flex gap-1.5">
                  <Input
                    id="tia-project-file"
                    className="h-8 min-w-0 flex-1 pl-9 text-xs md:text-xs"
                    value={projectFile}
                    onChange={event => setProjectFile(event.target.value)}
                    placeholder="C:\\Users\\…\\Documents\\Automation\\Line\\Line.ap17"
                  />
                  <Button
                    variant="outline"
                    size="sm"
                    className="w-[88px] shrink-0 justify-center text-xs"
                    type="button"
                    aria-label="Browse for TIA project file"
                    onClick={() => void browseProjectFile()}
                    disabled={browsing || busy}
                  >
                    {browsing ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <FolderOpen className="h-3.5 w-3.5" />}
                    Browse
                  </Button>
                </div>
              </div>
              <span className="text-xs leading-4 text-muted-foreground">A new TIA Portal instance is launched with this project open.</span>
              {browseError && <span className="text-xs leading-4 text-amber-500">{browseError}</span>}
              {trimmedProjectFile && !projectFileValid && (
                <span className="text-xs leading-4 text-amber-500">Enter the full path to a TIA Portal project file (.ap17).</span>
              )}
              {projectFileOutsideSandbox && (
                <span className="text-xs leading-4 text-amber-500">This path is outside the sandbox whitelist. Move the project under an allowed root, or creation will be denied.</span>
              )}
            </div>
          )}

          <div className="space-y-1.5">
            <Label htmlFor="workbench-root" className="text-xs">Custom root <em className="font-normal text-muted-foreground">optional</em></Label>
            <div className="relative">
              <FolderOpen className="absolute left-3 top-2.5 h-3.5 w-3.5 text-muted-foreground" />
              <Input
                id="workbench-root"
                className="h-8 pl-9 text-xs md:text-xs"
                value={rootPath}
                onChange={event => setRootPath(event.target.value)}
                placeholder="D:\\Automation\\MyWorkbench"
              />
            </div>
          </div>

          <div className="rounded-lg border bg-muted/40 p-3" style={{ borderColor: 'var(--border)' }}>
            <div className="text-xs font-medium uppercase tracking-[0.12em] text-muted-foreground">Resolved location</div>
            <div className="mt-1 break-all text-xs">{rootPath.trim() || defaultPreview}</div>
          </div>
        </div>

        <DialogFooter className="items-center justify-between border-t bg-muted/25 px-5 py-3 sm:flex-row">
          <OperationStatusLine
            status={operationStatus}
            fallback={busy ? 'Preparing workbench storage...' : undefined}
            onDismiss={onDismissOperation}
          />
          <div className="flex gap-2">
            <Button variant="outline" size="sm" onClick={onClose} disabled={busy}>Cancel</Button>
            <Button
              size="sm"
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
            </Button>
          </div>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
