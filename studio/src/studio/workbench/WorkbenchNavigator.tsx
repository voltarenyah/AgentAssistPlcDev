import {
  Boxes,
  ChevronDown,
  ChevronRight,
  Cpu,
  Database,
  GitBranch,
  Plus,
  RefreshCw,
} from 'lucide-react'
import type { Workbench, WorkbenchRegistration } from '@/api/client'

export type WorkbenchSelection = {
  workbenchId: string | null
  worktreeId: string | null
  deviceId: string | null
}

type Props = {
  workbenches: Workbench[]
  devicesByWorktree: Record<string, string[]>
  selection: WorkbenchSelection
  knowledgeState: Record<string, 'current' | 'stale' | 'missing' | 'failed'>
  loading: boolean
  onCreateWorkbench: () => void
  onCreateWorktree: (workbench: Workbench) => void
  onRefresh: () => void
  onSelectWorkbench: (workbench: Workbench) => void
  onSelectWorktree: (workbench: Workbench, worktree: WorkbenchRegistration) => void
  onSelectDevice: (workbench: Workbench, worktree: WorkbenchRegistration, deviceId: string) => void
}

const worktreeKey = (workbenchId: string, worktreeId: string) => `${workbenchId}:${worktreeId}`

export default function WorkbenchNavigator({
  workbenches,
  devicesByWorktree,
  selection,
  knowledgeState,
  loading,
  onCreateWorkbench,
  onCreateWorktree,
  onRefresh,
  onSelectWorkbench,
  onSelectWorktree,
  onSelectDevice,
}: Props) {
  return (
    <aside className="flex min-h-0 w-[310px] shrink-0 flex-col border-r bg-sidebar" style={{ borderColor: 'var(--border)' }}>
      <div className="flex h-12 items-center gap-2 border-b px-3" style={{ borderColor: 'var(--border)' }}>
        <Boxes className="h-4 w-4 text-chart-2" />
        <div className="min-w-0 flex-1">
          <div className="text-[11px] font-semibold tracking-wide">AUTOMATION WORKBENCH</div>
          <div className="text-[9px] text-muted-foreground">Shared history · isolated device context</div>
        </div>
        <button className="icon-button" title="Refresh workbenches" onClick={onRefresh}>
          <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
        </button>
        <button className="icon-button" title="Create workbench" onClick={onCreateWorkbench}>
          <Plus className="h-3.5 w-3.5" />
        </button>
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto p-2">
        {workbenches.length === 0 ? (
          <button
            onClick={onCreateWorkbench}
            className="group flex w-full flex-col items-start rounded-lg border border-dashed p-4 text-left hover:bg-accent/40"
            style={{ borderColor: 'var(--border)' }}
          >
            <span className="text-[11px] font-medium">Create your first workbench</span>
            <span className="mt-1 text-[9px] leading-relaxed text-muted-foreground">
              A workbench owns the shared Git repository, linked worktrees, PLC devices, and their knowledge databases.
            </span>
          </button>
        ) : workbenches.map(workbench => {
          const workbenchSelected = selection.workbenchId === workbench.workbenchId
          return (
            <section key={workbench.workbenchId} className="mb-1">
              <div
                className={`group flex cursor-pointer items-center gap-1.5 rounded-md px-2 py-1.5 ${workbenchSelected ? 'bg-accent' : 'hover:bg-accent/50'}`}
                onClick={() => onSelectWorkbench(workbench)}
              >
                {workbenchSelected ? <ChevronDown className="h-3 w-3" /> : <ChevronRight className="h-3 w-3" />}
                <Boxes className="h-3.5 w-3.5 text-chart-2" />
                <span className="min-w-0 flex-1 truncate text-[11px] font-medium">{workbench.name}</span>
                <button
                  className="icon-button opacity-0 group-hover:opacity-100"
                  title="New linked worktree"
                  onClick={event => {
                    event.stopPropagation()
                    onCreateWorktree(workbench)
                  }}
                >
                  <Plus className="h-3 w-3" />
                </button>
              </div>

              {workbenchSelected && (
                <div className="ml-4 border-l pl-2" style={{ borderColor: 'var(--border)' }}>
                  {workbench.worktrees.map(worktree => {
                    const worktreeSelected = selection.worktreeId === worktree.worktreeId
                    const key = worktreeKey(workbench.workbenchId, worktree.worktreeId)
                    const devices = devicesByWorktree[key] ?? []
                    return (
                      <div key={worktree.worktreeId}>
                        <div
                          className={`flex cursor-pointer items-center gap-1.5 rounded px-2 py-1.5 ${worktreeSelected ? 'bg-accent/80' : 'hover:bg-accent/40'}`}
                          onClick={() => onSelectWorktree(workbench, worktree)}
                        >
                          {worktreeSelected ? <ChevronDown className="h-3 w-3" /> : <ChevronRight className="h-3 w-3" />}
                          <GitBranch className="h-3.5 w-3.5 text-chart-4" />
                          <span className="min-w-0 flex-1 truncate text-[10px]">{worktree.name}</span>
                          <span className="rounded bg-muted px-1.5 py-0.5 font-mono text-[8px] text-muted-foreground">
                            {worktree.branch}
                          </span>
                        </div>
                        {worktreeSelected && (
                          <div className="ml-4 border-l pl-2" style={{ borderColor: 'var(--border)' }}>
                            {devices.length === 0 ? (
                              <div className="px-2 py-2 text-[9px] text-muted-foreground">No registered PLC devices</div>
                            ) : devices.map(deviceId => {
                              const selected = selection.deviceId === deviceId
                              const state = knowledgeState[deviceId] ?? 'missing'
                              return (
                                <button
                                  key={deviceId}
                                  onClick={() => onSelectDevice(workbench, worktree, deviceId)}
                                  className={`flex w-full items-center gap-2 rounded px-2 py-1.5 text-left ${selected ? 'bg-chart-2/10 ring-1 ring-chart-2/30' : 'hover:bg-accent/40'}`}
                                >
                                  <Cpu className={`h-3.5 w-3.5 ${selected ? 'text-chart-2' : 'text-muted-foreground'}`} />
                                  <span className="min-w-0 flex-1 truncate font-mono text-[9px]">{deviceId}</span>
                                  <Database className={`h-3 w-3 ${
                                    state === 'current' ? 'text-emerald-500'
                                      : state === 'stale' ? 'text-amber-500'
                                        : state === 'failed' ? 'text-red-500'
                                          : 'text-muted-foreground'
                                  }`} />
                                </button>
                              )
                            })}
                          </div>
                        )}
                      </div>
                    )
                  })}
                </div>
              )}
            </section>
          )
        })}
      </div>
    </aside>
  )
}
