import { useMemo, useState } from 'react'
import { Boxes, FolderOpen, Loader2, X } from 'lucide-react'
import type { OperationStatus, SessionInfo } from '@/api/client'
import OperationStatusLine from '@/studio/workbench/OperationStatusLine'

type Props = {
  sessions: SessionInfo[]
  busy: boolean
  operationStatus: OperationStatus | null
  onDismissOperation: () => void
  onClose: () => void
  onCreate: (values: {
    name: string
    rootPath?: string
    engineeringSessionId: number
    engineeringProjectPath: string
  }) => Promise<void>
}

const sanitized = (name: string) =>
  name.trim()
    .split('')
    .map(character => character.charCodeAt(0) < 32 || '<>:"/\\|?*'.includes(character) ? '-' : character)
    .join('')
    .replace(/[. ]+$/g, '') || '<workbench-name>'

export default function CreateWorkbenchDialog({
  sessions,
  busy,
  operationStatus,
  onDismissOperation,
  onClose,
  onCreate,
}: Props) {
  const [name, setName] = useState('')
  const [rootPath, setRootPath] = useState('')
  const [sessionId, setSessionId] = useState(sessions[0]?.id?.toString() ?? '')
  const selectedSession = sessions.find(session => session.id.toString() === sessionId)
  const defaultPreview = useMemo(
    () => `%LOCALAPPDATA%\\AutomationWorkbench\\Project\\${sanitized(name)}`,
    [name],
  )
  const projectPath = selectedSession?.projectPath ?? ''
  const valid = Boolean(name.trim() && projectPath)

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
          <button className="icon-button" onClick={onClose}><X className="h-4 w-4" /></button>
        </div>

        <div className="space-y-4 p-5">
          <label className="field-label">
            <span>Workbench name</span>
            <input className="field-input" value={name} onChange={event => setName(event.target.value)} placeholder="Line-7 commissioning" autoFocus />
          </label>

          <label className="field-label">
            <span>TIA project</span>
            <select className="field-input" value={sessionId} onChange={event => setSessionId(event.target.value)}>
              <option value="">Select an open TIA project…</option>
              {sessions.map(session => (
                <option key={session.id} value={session.id}>
                  PID {session.id} · {session.projectPath ?? 'No project loaded'}
                </option>
              ))}
            </select>
            {sessions.length === 0 && (
              <span className="text-[9px] text-amber-500">Open the engineering project in TIA Portal, then refresh sessions.</span>
            )}
          </label>

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
              onClick={() => onCreate({
                name: name.trim(),
                rootPath: rootPath.trim() || undefined,
                engineeringSessionId: Number(sessionId),
                engineeringProjectPath: projectPath,
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
