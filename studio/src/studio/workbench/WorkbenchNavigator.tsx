import {
  Archive,
  Boxes,
  CircuitBoard,
  ChevronDown,
  ChevronRight,
  Cpu,
  Database,
  GitBranch,
  GitMerge,
  Monitor,
  MonitorOff,
  Plus,
  RefreshCw,
  RotateCw,
  ShieldCheck,
  Trash2,
} from 'lucide-react'
import type { ReactNode } from 'react'
import type { DeviceSummary, Workbench, WorkbenchRegistration, WorkbenchTagSearchResults } from '@/api/client'
import {
  ContextMenu,
  ContextMenuContent,
  ContextMenuItem,
  ContextMenuLabel,
  ContextMenuSeparator,
  ContextMenuTrigger,
} from '@/components/ui/context-menu'

export type WorkbenchSelection = {
  workbenchId: string | null
  worktreeId: string | null
  deviceId: string | null
}

type Props = {
  workbenches: Workbench[]
  devicesByWorktree: Record<string, DeviceSummary[]>
  selection: WorkbenchSelection
  /** Which page <main> is currently showing; drives the active-row highlight. */
  viewKind: 'project' | 'worktree' | 'hardware' | 'device'
  knowledgeState: Record<string, 'current' | 'stale' | 'missing' | 'failed'>
  loading: boolean
  /** A tag search is active; rows come only from the server-owned result below. */
  filterActive?: boolean
  /** Server-owned tag search identities and availability, never client-derived tag semantics. */
  filteredResults?: WorkbenchTagSearchResults | null
  /** Filter controls are owned by the caller but placed directly below the navigator header. */
  filterControl?: ReactNode
  onCreateWorkbench: () => void
  onCreateWorktree: (workbench: Workbench) => void
  onOpenWorkbench: (workbench: Workbench, upgrade: boolean) => void
  onOpenWorktree: (workbench: Workbench, worktree: WorkbenchRegistration, upgrade: boolean) => void
  onInspectWorkbench: (workbench: Workbench) => void
  onInspectWorktree: (workbench: Workbench, worktree: WorkbenchRegistration) => void
  onArchiveWorktree: (workbench: Workbench, worktree: WorkbenchRegistration) => void
  onRefresh: () => void
  onSelectWorkbench: (workbench: Workbench) => void
  onSelectWorktree: (workbench: Workbench, worktree: WorkbenchRegistration) => void
  onSelectDevice: (workbench: Workbench, worktree: WorkbenchRegistration, deviceId: string) => void
  onSelectHardware: (workbench: Workbench, worktree: WorkbenchRegistration) => void
  onReloadHardware: (workbench: Workbench, worktree: WorkbenchRegistration) => void
  onCompareHardware: (workbench: Workbench, worktree: WorkbenchRegistration) => void
  onDeleteWorkbench: (workbench: Workbench) => void
  onDeleteWorktree: (workbench: Workbench, worktree: WorkbenchRegistration) => void
  onMergeWorktree: (workbench: Workbench, worktree: WorkbenchRegistration) => void
  onOpenDevice: (workbench: Workbench, worktree: WorkbenchRegistration, deviceId: string, withUI: boolean) => void
  onUpgradeDevice: (workbench: Workbench, worktree: WorkbenchRegistration, deviceId: string) => void
  onInspectDevice: (workbench: Workbench, worktree: WorkbenchRegistration, deviceId: string) => void
  onCompareDevice: (workbench: Workbench, worktree: WorkbenchRegistration, deviceId: string) => void
  onRebuildDevice: (workbench: Workbench, worktree: WorkbenchRegistration, deviceId: string) => void
  onUpdateKnowledge: (workbench: Workbench, worktree: WorkbenchRegistration, deviceId: string) => void
  onRebuildKnowledge: (workbench: Workbench, worktree: WorkbenchRegistration, deviceId: string) => void
}

const worktreeKey = (workbenchId: string, worktreeId: string) => `${workbenchId}:${worktreeId}`

export default function WorkbenchNavigator({
  workbenches,
  devicesByWorktree,
  selection,
  viewKind,
  knowledgeState,
  loading,
  filterActive = false,
  filteredResults = null,
  filterControl,
  onCreateWorkbench,
  onCreateWorktree,
  onOpenWorkbench,
  onOpenWorktree,
  onInspectWorkbench,
  onInspectWorktree,
  onArchiveWorktree,
  onRefresh,
  onSelectWorkbench,
  onSelectWorktree,
  onSelectDevice,
  onSelectHardware,
  onReloadHardware,
  onCompareHardware,
  onDeleteWorkbench,
  onDeleteWorktree,
  onMergeWorktree,
  onOpenDevice,
  onUpgradeDevice,
  onInspectDevice,
  onCompareDevice,
  onRebuildDevice,
  onUpdateKnowledge,
  onRebuildKnowledge,
}: Props) {
  const matchingWorkbenchIds = new Set(filteredResults?.workbenches.map(result => result.entityId) ?? [])
  const matchingWorktrees = new Map(
    (filteredResults?.worktrees ?? []).map(result => [result.entityId, result]),
  )
  const visibleWorkbenches = filterActive
    ? workbenches.filter(workbench => matchingWorkbenchIds.has(workbench.workbenchId)
      || workbench.worktrees.some(worktree => matchingWorktrees.has(worktree.worktreeId)))
    : workbenches

  return (
    <aside data-dock-content="left" className="flex h-full min-h-0 w-full shrink-0 flex-col border-r bg-sidebar" style={{ borderColor: 'var(--border)' }}>
      <div className="flex h-12 items-center gap-2 border-b px-3" style={{ borderColor: 'var(--border)' }}>
        <Boxes className="h-4 w-4 text-chart-2" />
        <div className="min-w-0 flex-1 text-[11px] font-semibold tracking-wide">Projects</div>
        <button className="icon-button" title="Refresh workbenches" onClick={onRefresh}>
          <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
        </button>
        <button className="icon-button" title="Create workbench" onClick={onCreateWorkbench}>
          <Plus className="h-3.5 w-3.5" />
        </button>
      </div>
      {filterControl}

      <div className="scrollbar-sleek min-h-0 flex-1 overflow-y-auto p-2">
        {visibleWorkbenches.length === 0 ? filterActive ? (
          <div className="px-2 py-3 text-[10px] text-muted-foreground" role="status">
            {filteredResults ? 'No projects or worktrees match these tags.' : 'Filtering projects and worktrees…'}
          </div>
        ) : (
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
        ) : visibleWorkbenches.map(workbench => {
          const workbenchSelected = selection.workbenchId === workbench.workbenchId
          const workbenchExpanded = workbenchSelected || filterActive
          const visibleWorktrees = filterActive
            ? workbench.worktrees.filter(worktree => matchingWorktrees.has(worktree.worktreeId))
            : workbench.worktrees
          return (
            <section key={workbench.workbenchId} className="mb-1">
              <ContextMenu>
                <ContextMenuTrigger asChild>
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
                </ContextMenuTrigger>
                <ContextMenuContent>
                  <ContextMenuLabel>{workbench.name}</ContextMenuLabel>
                  <ContextMenuItem onSelect={() => onCreateWorktree(workbench)}>
                    <Plus className="h-3.5 w-3.5" />
                    New linked worktree
                  </ContextMenuItem>
                  <ContextMenuItem onSelect={() => onOpenWorkbench(workbench, false)}>
                    <Monitor className="h-3.5 w-3.5" />
                    Open TIA with UI
                  </ContextMenuItem>
                  <ContextMenuItem onSelect={() => onOpenWorkbench(workbench, true)}>
                    <RotateCw className="h-3.5 w-3.5" />
                    Open TIA with upgrade
                  </ContextMenuItem>
                  <ContextMenuItem onSelect={() => onInspectWorkbench(workbench)}>
                    <ShieldCheck className="h-3.5 w-3.5" />
                    Inspect TIA access
                  </ContextMenuItem>
                  <ContextMenuItem onSelect={onRefresh}>
                    <RefreshCw className="h-3.5 w-3.5" />
                    Refresh project tree
                  </ContextMenuItem>
                  <ContextMenuSeparator />
                  <ContextMenuItem
                    variant="destructive"
                    onSelect={() => onDeleteWorkbench(workbench)}
                  >
                    <Trash2 className="h-3.5 w-3.5" />
                    Delete this project
                  </ContextMenuItem>
                </ContextMenuContent>
              </ContextMenu>

              {workbenchExpanded && (
                <div className="ml-4 border-l pl-2" style={{ borderColor: 'var(--border)' }}>
                  {visibleWorktrees.map(worktree => {
                    const worktreeSelected = selection.worktreeId === worktree.worktreeId
                    const available = matchingWorktrees.get(worktree.worktreeId)?.available ?? true
                    const key = worktreeKey(workbench.workbenchId, worktree.worktreeId)
                    const devices = devicesByWorktree[key] ?? []
                    return (
                      <div key={worktree.worktreeId}>
                        <ContextMenu>
                          <ContextMenuTrigger asChild>
                            <div
                              className={`flex cursor-pointer items-center gap-1.5 rounded px-2 py-1.5 ${
                                worktreeSelected && viewKind === 'worktree'
                                  ? 'bg-chart-2/10 ring-1 ring-chart-2/30'
                                  : worktreeSelected
                                    ? 'bg-accent/80'
                                    : 'hover:bg-accent/40'
                              }`}
                              onClick={() => onSelectWorktree(workbench, worktree)}
                            >
                              {worktreeSelected ? <ChevronDown className="h-3 w-3" /> : <ChevronRight className="h-3 w-3" />}
                              <GitBranch className="h-3.5 w-3.5 text-chart-4" />
                              <span className="min-w-0 flex-1 truncate text-[10px]">{worktree.name}</span>
                              <span className="rounded bg-muted px-1.5 py-0.5 font-mono text-[8px] text-muted-foreground">
                                {worktree.branch}
                              </span>
                              {!available && (
                                <span
                                  className="rounded bg-amber-500/10 px-1.5 py-0.5 text-[8px] text-amber-600 dark:text-amber-400"
                                  title="Registered worktree directory is unavailable"
                                  data-worktree-availability="unavailable"
                                >
                                  Unavailable
                                </span>
                              )}
                            </div>
                          </ContextMenuTrigger>
                          <ContextMenuContent>
                            <ContextMenuLabel>{worktree.name}</ContextMenuLabel>
                            <ContextMenuItem onSelect={() => onSelectWorktree(workbench, worktree)}>
                              <GitBranch className="h-3.5 w-3.5" />
                              Select worktree
                            </ContextMenuItem>
                            <ContextMenuItem onSelect={() => onCreateWorktree(workbench)}>
                              <Plus className="h-3.5 w-3.5" />
                              New linked worktree
                            </ContextMenuItem>
                            <ContextMenuItem onSelect={() => onOpenWorktree(workbench, worktree, false)}>
                              <Monitor className="h-3.5 w-3.5" />
                              Open TIA with UI
                            </ContextMenuItem>
                            <ContextMenuItem onSelect={() => onOpenWorktree(workbench, worktree, true)}>
                              <RotateCw className="h-3.5 w-3.5" />
                              Open TIA with upgrade
                            </ContextMenuItem>
                            <ContextMenuItem onSelect={() => onInspectWorktree(workbench, worktree)}>
                              <ShieldCheck className="h-3.5 w-3.5" />
                              Inspect TIA access
                            </ContextMenuItem>
                            <ContextMenuItem onSelect={() => onArchiveWorktree(workbench, worktree)}>
                              <Archive className="h-3.5 w-3.5" />
                              Archive TIA project
                            </ContextMenuItem>
                            <ContextMenuItem
                              disabled={worktree.branch === 'master'}
                              onSelect={() => onMergeWorktree(workbench, worktree)}
                            >
                              <GitMerge className="h-3.5 w-3.5" />
                              Validate and merge to master
                            </ContextMenuItem>
                            <ContextMenuSeparator />
                            <ContextMenuItem
                              disabled={worktree.branch === 'master'}
                              variant="destructive"
                              onSelect={() => onDeleteWorktree(workbench, worktree)}
                            >
                              <Trash2 className="h-3.5 w-3.5" />
                              Remove worktree
                            </ContextMenuItem>
                          </ContextMenuContent>
                        </ContextMenu>
                        {worktreeSelected && (
                          <div className="ml-4 border-l pl-2" style={{ borderColor: 'var(--border)' }}>
                            <ContextMenu>
                              <ContextMenuTrigger asChild>
                                <button
                                  onClick={() => onSelectHardware(workbench, worktree)}
                                  className={`flex w-full items-center gap-2 rounded px-2 py-1.5 text-left ${
                                    viewKind === 'hardware' ? 'bg-chart-2/10 ring-1 ring-chart-2/30' : 'hover:bg-accent/40'
                                  }`}
                                >
                                  <CircuitBoard className={`h-3.5 w-3.5 ${
                                    viewKind === 'hardware' ? 'text-chart-2' : 'text-muted-foreground'
                                  }`} />
                                  <span className="min-w-0 flex-1 truncate text-[10px]">Hardware configuration</span>
                                </button>
                              </ContextMenuTrigger>
                              <ContextMenuContent>
                                <ContextMenuLabel>Hardware configuration</ContextMenuLabel>
                                <ContextMenuItem onSelect={() => onSelectHardware(workbench, worktree)}>
                                  <CircuitBoard className="h-3.5 w-3.5" />
                                  Select hardware configuration
                                </ContextMenuItem>
                                <ContextMenuSeparator />
                                <ContextMenuItem onSelect={() => onReloadHardware(workbench, worktree)}>
                                  <RotateCw className="h-3.5 w-3.5" />
                                  Reload hardware configuration
                                </ContextMenuItem>
                                <ContextMenuItem onSelect={() => onCompareHardware(workbench, worktree)}>
                                  <RefreshCw className="h-3.5 w-3.5" />
                                  Compare hardware with TIA
                                </ContextMenuItem>
                              </ContextMenuContent>
                            </ContextMenu>
                            {devices.length === 0 ? (
                              <div className="px-2 py-2 text-[9px] text-muted-foreground">No registered PLC devices</div>
                            ) : devices.map(device => {
                              const selected = selection.deviceId === device.deviceId
                              const state = knowledgeState[device.deviceId] ?? 'missing'
                              return (
                                <ContextMenu key={device.deviceId}>
                                  <ContextMenuTrigger asChild>
                                    <button
                                      title={device.deviceId}
                                      onClick={() => onSelectDevice(workbench, worktree, device.deviceId)}
                                      className={`flex w-full items-center gap-2 rounded px-2 py-1.5 text-left ${selected ? 'bg-chart-2/10 ring-1 ring-chart-2/30' : 'hover:bg-accent/40'}`}
                                    >
                                      <Cpu className={`h-3.5 w-3.5 ${selected ? 'text-chart-2' : 'text-muted-foreground'}`} />
                                      <span className="min-w-0 flex-1 truncate text-[10px]">{device.plcName}</span>
                                      <Database className={`h-3 w-3 ${
                                        state === 'current' ? 'text-emerald-500'
                                          : state === 'stale' ? 'text-amber-500'
                                            : state === 'failed' ? 'text-red-500'
                                              : 'text-muted-foreground'
                                      }`} />
                                    </button>
                                  </ContextMenuTrigger>
                                  <ContextMenuContent>
                                    <ContextMenuLabel>{device.plcName}</ContextMenuLabel>
                                    <ContextMenuItem onSelect={() => onSelectDevice(workbench, worktree, device.deviceId)}>
                                      <Cpu className="h-3.5 w-3.5" />
                                      Select device
                                    </ContextMenuItem>
                                    <ContextMenuSeparator />
                                    <ContextMenuItem onSelect={() => onOpenDevice(workbench, worktree, device.deviceId, true)}>
                                      <Monitor className="h-3.5 w-3.5" />
                                      Open TIA with UI
                                    </ContextMenuItem>
                                    <ContextMenuItem onSelect={() => onOpenDevice(workbench, worktree, device.deviceId, false)}>
                                      <MonitorOff className="h-3.5 w-3.5" />
                                      Open TIA headless
                                    </ContextMenuItem>
                                    <ContextMenuItem onSelect={() => onUpgradeDevice(workbench, worktree, device.deviceId)}>
                                      <RotateCw className="h-3.5 w-3.5" />
                                      Open TIA with upgrade
                                    </ContextMenuItem>
                                    <ContextMenuItem onSelect={() => onInspectDevice(workbench, worktree, device.deviceId)}>
                                      <ShieldCheck className="h-3.5 w-3.5" />
                                      Inspect TIA access
                                    </ContextMenuItem>
                                    <ContextMenuItem onSelect={() => onCompareDevice(workbench, worktree, device.deviceId)}>
                                      <RefreshCw className="h-3.5 w-3.5" />
                                      Compare with TIA
                                    </ContextMenuItem>
                                    <ContextMenuItem onSelect={() => onRebuildDevice(workbench, worktree, device.deviceId)}>
                                      <RotateCw className="h-3.5 w-3.5" />
                                      Rebuild project
                                    </ContextMenuItem>
                                    <ContextMenuSeparator />
                                    <ContextMenuItem onSelect={() => onUpdateKnowledge(workbench, worktree, device.deviceId)}>
                                      <Database className="h-3.5 w-3.5" />
                                      Update knowledge
                                    </ContextMenuItem>
                                    <ContextMenuItem onSelect={() => onRebuildKnowledge(workbench, worktree, device.deviceId)}>
                                      <Database className="h-3.5 w-3.5" />
                                      Rebuild knowledge
                                    </ContextMenuItem>
                                  </ContextMenuContent>
                                </ContextMenu>
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
