import { CircleDot, Eye } from 'lucide-react'
import type { AppAssistantRuntimeSnapshot } from '@/api/client'

type Props = {
  runtime: AppAssistantRuntimeSnapshot | null
}

const operationStatus = (status: string | number) => {
  if (typeof status === 'number') {
    return ['idle', 'running', 'awaiting approval', 'succeeded', 'failed', 'cancelled'][status] ?? `status ${status}`
  }
  return status.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/[-_]/g, ' ').toLowerCase()
}

const value = (item: string | number | null | undefined) => item === null || item === undefined || item === '' ? 'unknown' : String(item)

export default function RuntimeStateStatusBar({ runtime }: Props) {
  if (!runtime) {
    return (
      <details className="studio-status-item runtime-state-popover" data-runtime-state>
        <summary className="runtime-state-summary" title="The shared runtime state has not been loaded">
          <CircleDot className="h-3 w-3 text-amber-500" /> Runtime state unavailable
        </summary>
        <div className="runtime-state-panel">Waiting for the shared runtime snapshot.</div>
      </details>
    )
  }

  const focus = runtime.focus ?? { worktreeId: null, deviceId: null }
  const operationState = runtime.operation ?? { kind: null, status: 'idle' as const }
  const worktree = runtime.worktrees?.find(item => item.worktreeId === focus.worktreeId)
  const device = worktree?.devices?.find(item => item.deviceId === focus.deviceId)
  const operation = operationStatus(operationState.status)

  return (
    <details className="studio-status-item runtime-state-popover" data-runtime-state>
      <summary className="runtime-state-summary" title="Inspect the shared runtime state">
        <CircleDot className="h-3 w-3 text-chart-4" />
        Runtime rev {runtime.workbenchRevision}
        <span className="text-muted-foreground">· {operation}</span>
        {worktree && <span className="text-muted-foreground">· Git {value(worktree.gitStatus)}</span>}
      </summary>
      <div className="runtime-state-panel" aria-label="Shared runtime state details">
        <div className="runtime-state-heading"><Eye className="h-3 w-3" /> Shared runtime state</div>
        <div className="runtime-state-grid">
          <span>Revision</span><strong>{runtime.workbenchRevision}</strong>
          <span>Operation</span><strong>{value(operationState.kind)} · {operation}</strong>
          <span>Focus</span><strong>{value(focus.worktreeId)} / {value(focus.deviceId)}</strong>
          <span>Git</span><strong>{value(worktree?.gitStatus)} · {value(worktree?.head?.slice(0, 8))}</strong>
          <span>Todos</span><strong>{worktree?.todoCount ?? 'unknown'}</strong>
          <span>SVN</span><strong>SVN {worktree?.svnBaseRevision ?? 'unknown'} → {worktree?.svnCurrentRevision ?? 'unknown'}</strong>
          <span>Validation</span><strong>Validation {value(worktree?.validationState)}</strong>
          <span>Device</span><strong>{device ? `${value(device.plcName ?? device.deviceId)} · TIA ${value(device.tiaState)} · Knowledge ${value(device.knowledgeFreshness)}` : 'unknown'}</strong>
        </div>
        <time dateTime={runtime.observedAt}>Observed {new Date(runtime.observedAt).toLocaleString()}</time>
      </div>
    </details>
  )
}
