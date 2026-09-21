import {
  Archive,
  CircleDot,
  CircuitBoard,
  Cpu,
  Database,
  Ellipsis,
  Factory,
  FileText,
  GitBranch,
  GitMerge,
  House,
  Monitor,
  MonitorOff,
  Minus,
  Plus,
  Pencil,
  RefreshCw,
  RotateCw,
  ShieldCheck,
  Sparkles,
  Trash2,
  Wrench,
} from 'lucide-react'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import type { DeviceSummary, EngineeringTask, Workbench, WorkbenchRegistration, WorkbenchTagSearchResults, WorktreeTaskStatus } from '@/api/client'
import {
  ContextMenu,
  ContextMenuContent,
  ContextMenuItem,
  ContextMenuLabel,
  ContextMenuSeparator,
  ContextMenuTrigger,
} from '@/components/ui/context-menu'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuSub,
  DropdownMenuSubContent,
  DropdownMenuSubTrigger,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Input } from '@/components/ui/input'

export type WorkbenchSelection = {
  workbenchId: string | null
  worktreeId: string | null
  deviceId: string | null
}

type TaskUpdate = { title: string; type: EngineeringTask['type']; status: WorktreeTaskStatus }

type Props = {
  workbenches: Workbench[]
  devicesByWorktree: Record<string, DeviceSummary[]>
  tasksByWorktree?: Record<string, EngineeringTask[]>
  activeTaskId?: string | null
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
  onShowHome: () => void
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
  onSelectTask?: (workbench: Workbench, worktree: WorkbenchRegistration, task: EngineeringTask) => void
  onUpdateTask?: (workbench: Workbench, worktree: WorkbenchRegistration, task: EngineeringTask, update: Partial<TaskUpdate>) => void
  onAddTask?: (workbench: Workbench, worktree: WorkbenchRegistration) => void
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
const taskStatusDotClass = (status: string) =>
  status === 'inProgress' || status === 'active' ? 'bg-emerald-500'
    : status === 'done' ? 'bg-muted-foreground'
      : 'bg-muted-foreground'
const taskTypeIcon = {
  issue: CircleDot,
  improvement: Wrench,
  feature: Sparkles,
} satisfies Record<EngineeringTask['type'], typeof FileText>
// Device actions remain wired while their task-page replacements are introduced.
// The device tree itself is deliberately not part of the navigator anymore.
const showLegacyDeviceTree = false

export default function WorkbenchNavigator({
  workbenches,
  devicesByWorktree,
  tasksByWorktree = {},
  activeTaskId = null,
  selection,
  viewKind,
  knowledgeState,
  loading,
  filterActive = false,
  filteredResults = null,
  filterControl,
  onShowHome,
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
  onSelectTask = () => {},
  onUpdateTask = () => {},
  onAddTask = () => {},
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
  const [expandedWorkbenchIds, setExpandedWorkbenchIds] = useState<Set<string>>(
    () => new Set(selection.workbenchId ? [selection.workbenchId] : []),
  )
  const [expandedWorktreeIds, setExpandedWorktreeIds] = useState<Set<string>>(
    () => new Set(selection.worktreeId ? [selection.worktreeId] : []),
  )
  const [clickedTaskId, setClickedTaskId] = useState<string | null>(null)
  const [renameTask, setRenameTask] = useState<{ workbench: Workbench; worktree: WorkbenchRegistration; task: EngineeringTask } | null>(null)
  const [renameTitle, setRenameTitle] = useState('')
  const previousSelectedWorkbenchId = useRef(selection.workbenchId)
  useEffect(() => {
    if (selection.workbenchId && selection.workbenchId !== previousSelectedWorkbenchId.current) {
      setExpandedWorkbenchIds(current => {
        if (current.has(selection.workbenchId!)) return current
        const next = new Set(current)
        next.add(selection.workbenchId!)
        return next
      })
    }
    previousSelectedWorkbenchId.current = selection.workbenchId
  }, [selection.workbenchId])
  const matchingWorkbenchIds = new Set(filteredResults?.workbenches.map(result => result.entityId) ?? [])
  const matchingWorktrees = new Map(
    (filteredResults?.worktrees ?? []).map(result => [result.entityId, result]),
  )
  const visibleWorkbenches = filterActive
    ? workbenches.filter(workbench => matchingWorkbenchIds.has(workbench.workbenchId)
      || workbench.worktrees.some(worktree => matchingWorktrees.has(worktree.worktreeId)))
    : workbenches
  const openRenameTask = (workbench: Workbench, worktree: WorkbenchRegistration, task: EngineeringTask) => {
    setRenameTitle(task.title)
    setRenameTask({ workbench, worktree, task })
  }
  const saveTaskRename = () => {
    if (!renameTask || !renameTitle.trim()) return
    onUpdateTask(renameTask.workbench, renameTask.worktree, renameTask.task, { title: renameTitle.trim() })
    setRenameTask(null)
  }

  return (
    <>
    <aside data-dock-content="left" className="flex h-full min-h-0 w-full shrink-0 flex-col border-r bg-sidebar" style={{ borderColor: 'var(--border)' }}>
      <div className="flex h-12 items-center gap-2 border-b px-3" style={{ borderColor: 'var(--border)' }}>
        <div className="flex min-w-0 flex-1 items-center gap-2">
          <div className="truncate text-sm font-semibold tracking-tight">Automation Workbench</div>
          <Button
            variant="ghost"
            size="icon-sm"
            aria-label="Go to all projects"
            title="All projects"
            onClick={onShowHome}
          >
            <House className="h-3.5 w-3.5" />
          </Button>
        </div>
        <Button variant="ghost" size="icon-sm" aria-label="Refresh workbenches" title="Refresh workbenches" onClick={onRefresh}>
          <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
        </Button>
        <Button variant="ghost" size="icon-sm" aria-label="Create workbench" title="Create workbench" onClick={onCreateWorkbench}>
          <Plus className="h-3.5 w-3.5" />
        </Button>
      </div>
      {filterControl}

      <div className="scrollbar-sleek min-h-0 flex-1 overflow-y-auto p-2">
        <div className="flex items-center px-1 pb-2 pt-1 text-[9px] font-semibold tracking-[0.18em] text-muted-foreground">
          <span>PROJECTS</span>
        </div>
        {visibleWorkbenches.length === 0 ? filterActive ? (
            <div className="px-2 py-3 text-xs leading-4 text-muted-foreground" role="status">
            {filteredResults ? 'No projects or worktrees match these tags.' : 'Filtering projects and worktrees…'}
          </div>
        ) : (
          <button
            onClick={onCreateWorkbench}
            className="group flex w-full flex-col items-start rounded-lg border border-dashed p-4 text-left transition-colors hover:bg-accent/40"
            style={{ borderColor: 'var(--border)' }}
          >
            <span className="text-xs font-medium">Create your first workbench</span>
            <span className="mt-1 text-xs leading-4 text-muted-foreground">
              A workbench owns the shared Git repository, linked worktrees, PLC devices, and their knowledge databases.
            </span>
          </button>
        ) : visibleWorkbenches.map(workbench => {
          const workbenchSelected = selection.workbenchId === workbench.workbenchId
          const workbenchExpanded = expandedWorkbenchIds.has(workbench.workbenchId) || filterActive
          const visibleWorktrees = filterActive
            ? workbench.worktrees.filter(worktree => matchingWorktrees.has(worktree.worktreeId))
            : workbench.worktrees
          return (
            <section key={workbench.workbenchId} className="mb-1">
              <ContextMenu>
                <ContextMenuTrigger asChild>
                  <div
                    className={`group flex min-h-8 cursor-pointer items-center gap-2 rounded-sm px-1 py-1 ${workbenchSelected ? 'bg-accent/50' : 'hover:bg-accent/40'}`}
                    onClick={() => {
                      setExpandedWorkbenchIds(current => {
                        const next = new Set(current)
                        if (next.has(workbench.workbenchId)) next.delete(workbench.workbenchId)
                        else next.add(workbench.workbenchId)
                        return next
                      })
                      onSelectWorkbench(workbench)
                    }}
                  >
                    {workbenchExpanded
                      ? <Minus aria-hidden="true" className="h-3 w-3 text-muted-foreground" />
                      : <Plus aria-hidden="true" className="h-3 w-3 text-muted-foreground" />}
                    <Factory className="h-4 w-4 text-muted-foreground" />
                    <span className="min-w-0 flex-1 truncate text-xs font-medium">{workbench.name}</span>
                    <DropdownMenu>
                      <DropdownMenuTrigger asChild>
                        <Button
                          variant="ghost"
                          size="icon-xs"
                          aria-label={`Project actions ${workbench.name}`}
                          onClick={event => event.stopPropagation()}
                        >
                          <Ellipsis className="h-3.5 w-3.5" />
                        </Button>
                      </DropdownMenuTrigger>
                      <DropdownMenuContent align="end">
                        <DropdownMenuLabel>{workbench.name}</DropdownMenuLabel>
                        <DropdownMenuItem onSelect={() => onCreateWorktree(workbench)}>
                          <Plus className="h-3.5 w-3.5" />
                          New linked worktree
                        </DropdownMenuItem>
                        <DropdownMenuItem onSelect={() => onOpenWorkbench(workbench, false)}>
                          <Monitor className="h-3.5 w-3.5" />
                          Open TIA with UI
                        </DropdownMenuItem>
                        <DropdownMenuItem onSelect={() => onOpenWorkbench(workbench, true)}>
                          <RotateCw className="h-3.5 w-3.5" />
                          Open TIA with upgrade
                        </DropdownMenuItem>
                        <DropdownMenuItem onSelect={() => onInspectWorkbench(workbench)}>
                          <ShieldCheck className="h-3.5 w-3.5" />
                          Inspect TIA access
                        </DropdownMenuItem>
                        <DropdownMenuItem onSelect={onRefresh}>
                          <RefreshCw className="h-3.5 w-3.5" />
                          Refresh project tree
                        </DropdownMenuItem>
                        <DropdownMenuSeparator />
                        <DropdownMenuItem variant="destructive" onSelect={() => onDeleteWorkbench(workbench)}>
                          <Trash2 className="h-3.5 w-3.5" />
                          Delete this project
                        </DropdownMenuItem>
                      </DropdownMenuContent>
                    </DropdownMenu>
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
                    const tasks = tasksByWorktree[key] ?? []
                    return (
                      <div key={worktree.worktreeId}>
                        <ContextMenu>
                          <ContextMenuTrigger asChild>
                              <div
                                className={`group flex min-h-8 cursor-pointer items-center gap-2 rounded-sm px-1 py-1 ${
                                worktreeSelected && viewKind === 'worktree'
                                  ? 'bg-accent'
                                  : worktreeSelected
                                    ? 'bg-accent/70'
                                    : 'hover:bg-accent/40'
                              }`}
                              onClick={() => {
                                setExpandedWorktreeIds(current => {
                                  const next = new Set(current)
                                  if (next.has(worktree.worktreeId)) next.delete(worktree.worktreeId)
                                  else next.add(worktree.worktreeId)
                                  return next
                                })
                                onSelectWorktree(workbench, worktree)
                              }}
                            >
                              {expandedWorktreeIds.has(worktree.worktreeId)
                                ? <Minus aria-hidden="true" className="h-3 w-3 text-muted-foreground" />
                                : <Plus aria-hidden="true" className="h-3 w-3 text-muted-foreground" />}
                              <GitBranch className="h-4 w-4 text-chart-4" />
                              <span className="min-w-0 flex-1 truncate text-xs">{worktree.name}</span>
                              {worktree.branch !== worktree.name && <span className="max-w-[24%] truncate whitespace-nowrap font-mono text-[10px] leading-4 text-muted-foreground">{worktree.branch}</span>}
                              {!available && (
                                <span
                                  className="max-w-[42%] truncate whitespace-nowrap rounded bg-amber-500/10 px-1.5 py-0.5 text-xs leading-4 text-amber-600 dark:text-amber-400"
                                  title="Registered worktree directory is unavailable"
                                  data-worktree-availability="unavailable"
                                >
                                  Unavailable
                                </span>
                              )}
                              <DropdownMenu>
                                <DropdownMenuTrigger asChild>
                                  <Button
                                    variant="ghost"
                                    size="icon-xs"
                                    aria-label={`Worktree actions ${worktree.name}`}
                                    onClick={event => event.stopPropagation()}
                                  >
                                    <Ellipsis className="h-3.5 w-3.5" />
                                  </Button>
                                </DropdownMenuTrigger>
                                <DropdownMenuContent align="end">
                                  <DropdownMenuLabel>{worktree.name}</DropdownMenuLabel>
                                  <DropdownMenuItem onSelect={() => onSelectWorktree(workbench, worktree)}>
                                    <GitBranch className="h-3.5 w-3.5" />
                                    Select worktree
                                  </DropdownMenuItem>
                                  <DropdownMenuItem onSelect={() => onCreateWorktree(workbench)}>
                                    <Plus className="h-3.5 w-3.5" />
                                    New linked worktree
                                  </DropdownMenuItem>
                                  <DropdownMenuItem onSelect={() => onOpenWorktree(workbench, worktree, false)}>
                                    <Monitor className="h-3.5 w-3.5" />
                                    Open TIA with UI
                                  </DropdownMenuItem>
                                  <DropdownMenuItem onSelect={() => onOpenWorktree(workbench, worktree, true)}>
                                    <RotateCw className="h-3.5 w-3.5" />
                                    Open TIA with upgrade
                                  </DropdownMenuItem>
                                  <DropdownMenuItem onSelect={() => onInspectWorktree(workbench, worktree)}>
                                    <ShieldCheck className="h-3.5 w-3.5" />
                                    Inspect TIA access
                                  </DropdownMenuItem>
                                  <DropdownMenuItem onSelect={() => onArchiveWorktree(workbench, worktree)}>
                                    <Archive className="h-3.5 w-3.5" />
                                    Archive TIA project
                                  </DropdownMenuItem>
                                  <DropdownMenuItem disabled={worktree.branch === 'master'} onSelect={() => onMergeWorktree(workbench, worktree)}>
                                    <GitMerge className="h-3.5 w-3.5" />
                                    Validate and merge to master
                                  </DropdownMenuItem>
                                  <DropdownMenuSeparator />
                                  <DropdownMenuItem disabled={worktree.branch === 'master'} variant="destructive" onSelect={() => onDeleteWorktree(workbench, worktree)}>
                                    <Trash2 className="h-3.5 w-3.5" />
                                    Remove worktree
                                  </DropdownMenuItem>
                                </DropdownMenuContent>
                              </DropdownMenu>
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
                        {worktreeSelected && expandedWorktreeIds.has(worktree.worktreeId) && (
                          <div className="ml-4 border-l py-0.5 pl-2" style={{ borderColor: 'var(--border)' }}>
                            {tasks.length === 0 ? (
                              <Button variant="ghost" size="xs" className="text-muted-foreground hover:text-foreground" onClick={() => onAddTask(workbench, worktree)}><Plus className="h-3 w-3" /> Add task</Button>
                            ) : tasks.map(task => {
                              const taskSelected = (activeTaskId ?? clickedTaskId) === task.taskId
                              const TaskIcon = taskTypeIcon[task.type]
                              return (
                              <div key={task.taskId} className="group relative">
                                <button type="button" onClick={() => { setClickedTaskId(task.taskId); onSelectTask(workbench, worktree, task) }} className={`relative flex min-h-8 w-full items-center gap-2 rounded-md border px-2 py-1 pr-8 text-left before:absolute before:-left-2 before:top-1/2 before:h-px before:w-2 before:bg-border hover:bg-accent/40 ${taskSelected ? 'border-ring/70' : 'border-transparent'}`} aria-label={`Open task ${task.title}`} aria-current={taskSelected ? 'page' : undefined} data-task-selected={taskSelected || undefined} data-task-type={task.type}>
                                  <TaskIcon className="h-4 w-4 shrink-0 text-muted-foreground" />
                                  <span data-task-status={task.status} className={`h-2 w-2 shrink-0 rounded-full ${taskStatusDotClass(task.status)}`} />
                                  <span className="min-w-0 flex-1 truncate text-xs">{task.title}</span>
                                </button>
                                <DropdownMenu>
                                  <DropdownMenuTrigger asChild>
                                    <Button variant="ghost" size="icon-xs" aria-label={`Task actions ${task.title}`} className="absolute right-1 top-1/2 -translate-y-1/2 opacity-0 pointer-events-none group-hover:pointer-events-auto group-hover:opacity-100 focus-visible:pointer-events-auto focus-visible:opacity-100 data-[state=open]:pointer-events-auto data-[state=open]:opacity-100" onClick={event => event.stopPropagation()}>
                                      <Ellipsis className="h-3.5 w-3.5" />
                                    </Button>
                                  </DropdownMenuTrigger>
                                  <DropdownMenuContent align="end">
                                    <DropdownMenuLabel>{task.title}</DropdownMenuLabel>
                                    <DropdownMenuSub>
                                      <DropdownMenuSubTrigger>Change status</DropdownMenuSubTrigger>
                                      <DropdownMenuSubContent>
                                        {(['todo', 'inProgress', 'done'] as const).map(status => <DropdownMenuItem key={status} onSelect={() => onUpdateTask(workbench, worktree, task, { status })}>{status === 'inProgress' ? 'In progress' : status[0].toUpperCase() + status.slice(1)}</DropdownMenuItem>)}
                                      </DropdownMenuSubContent>
                                    </DropdownMenuSub>
                                    <DropdownMenuSub>
                                      <DropdownMenuSubTrigger>Change type and icon</DropdownMenuSubTrigger>
                                      <DropdownMenuSubContent>
                                        {(['issue', 'improvement', 'feature'] as const).map(type => {
                                          const TypeIcon = taskTypeIcon[type]
                                          return <DropdownMenuItem key={type} onSelect={() => onUpdateTask(workbench, worktree, task, { type })}><TypeIcon />{type[0].toUpperCase() + type.slice(1)}</DropdownMenuItem>
                                        })}
                                      </DropdownMenuSubContent>
                                    </DropdownMenuSub>
                                    <DropdownMenuSeparator />
                                    <DropdownMenuItem onSelect={() => openRenameTask(workbench, worktree, task)}><Pencil />Rename task</DropdownMenuItem>
                                  </DropdownMenuContent>
                                </DropdownMenu>
                              </div>
                              )
                            })}
                            {tasks.length > 0 && <Button variant="ghost" size="xs" className="text-muted-foreground hover:text-foreground" onClick={() => onAddTask(workbench, worktree)}><Plus className="h-3 w-3" /> Add task</Button>}
                          </div>
                        )}
                        {showLegacyDeviceTree && worktreeSelected && expandedWorktreeIds.has(worktree.worktreeId) && (
                          <div className="ml-4 border-l pl-2" style={{ borderColor: 'var(--border)' }}>
                            <ContextMenu>
                              <ContextMenuTrigger asChild>
                                <button
                                  onClick={() => onSelectHardware(workbench, worktree)}
                                  className={`flex min-h-7 w-full items-center gap-2 rounded px-2 py-1 text-left ${
                                    viewKind === 'hardware' ? 'bg-accent ring-1 ring-border/60' : 'hover:bg-accent/40'
                                  }`}
                                >
                                  <CircuitBoard className={`h-3.5 w-3.5 ${
                                    viewKind === 'hardware' ? 'text-chart-2' : 'text-muted-foreground'
                                  }`} />
                                  <span className="min-w-0 flex-1 truncate text-xs">Hardware configuration</span>
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
                              <div className="px-2 py-2 text-xs leading-4 text-muted-foreground">No registered PLC devices</div>
                            ) : devices.map(device => {
                              const selected = selection.deviceId === device.deviceId
                              const state = knowledgeState[device.deviceId] ?? 'missing'
                              return (
                                <ContextMenu key={device.deviceId}>
                                  <ContextMenuTrigger asChild>
                                    <button
                                      title={device.deviceId}
                                      onClick={() => onSelectDevice(workbench, worktree, device.deviceId)}
                                      className={`flex min-h-7 w-full items-center gap-2 rounded px-2 py-1 text-left ${selected ? 'bg-accent ring-1 ring-border/60' : 'hover:bg-accent/40'}`}
                                    >
                                      <Cpu className={`h-3.5 w-3.5 ${selected ? 'text-chart-2' : 'text-muted-foreground'}`} />
                                      <span className="min-w-0 flex-1 truncate text-xs">{device.plcName}</span>
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
    <Dialog open={renameTask !== null} onOpenChange={open => { if (!open) setRenameTask(null) }}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Rename task</DialogTitle>
          <DialogDescription>Choose a concise task title for this worktree.</DialogDescription>
        </DialogHeader>
        <form className="space-y-4" onSubmit={event => { event.preventDefault(); saveTaskRename() }}>
          <Input aria-label="Task title" value={renameTitle} onChange={event => setRenameTitle(event.target.value)} autoFocus />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => setRenameTask(null)}>Cancel</Button>
            <Button type="submit">Save</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
    </>
  )
}
