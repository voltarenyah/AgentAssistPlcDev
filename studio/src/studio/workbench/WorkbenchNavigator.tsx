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
  MenuLabel,
  ContextMenuItem,
  ContextMenuSeparator,
  ContextMenuTrigger,
} from '@notion-kit/ui/primitives'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuSub,
  DropdownMenuSubTrigger,
  DropdownMenuTrigger,
} from '@notion-kit/ui/primitives'
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
                <ContextMenuTrigger>
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
                      <DropdownMenuTrigger render={<Button
                          variant="ghost"
                          size="icon-xs"
                          aria-label={`Project actions ${workbench.name}`}
                          onClick={event => event.stopPropagation()}
                        >
                          <Ellipsis className="h-3.5 w-3.5" />
                        </Button>} />
                      <DropdownMenuContent align="end" className="w-max min-w-56">
                        <MenuLabel title={workbench.name} />
                        <DropdownMenuItem icon={<Plus className="h-3.5 w-3.5" />} label="New linked worktree" onClick={() => onCreateWorktree(workbench)} />
                        <DropdownMenuItem icon={<Monitor className="h-3.5 w-3.5" />} label="Open TIA with UI" onClick={() => onOpenWorkbench(workbench, false)} />
                        <DropdownMenuItem icon={<RotateCw className="h-3.5 w-3.5" />} label="Open TIA with upgrade" onClick={() => onOpenWorkbench(workbench, true)} />
                        <DropdownMenuItem icon={<ShieldCheck className="h-3.5 w-3.5" />} label="Inspect TIA access" onClick={() => onInspectWorkbench(workbench)} />
                        <DropdownMenuItem icon={<RefreshCw className="h-3.5 w-3.5" />} label="Refresh project tree" onClick={onRefresh} />
                        <DropdownMenuSeparator />
                        <DropdownMenuItem variant="error" icon={<Trash2 className="h-3.5 w-3.5" />} label="Delete this project" onClick={() => onDeleteWorkbench(workbench)} />
                      </DropdownMenuContent>
                    </DropdownMenu>
                  </div>
                </ContextMenuTrigger>
                <ContextMenuContent className="w-max min-w-56">
                  <MenuLabel title={workbench.name} />
                  <ContextMenuItem icon={<Plus className="h-3.5 w-3.5" />} label="New linked worktree" onClick={() => onCreateWorktree(workbench)} />
                  <ContextMenuItem icon={<Monitor className="h-3.5 w-3.5" />} label="Open TIA with UI" onClick={() => onOpenWorkbench(workbench, false)} />
                  <ContextMenuItem icon={<RotateCw className="h-3.5 w-3.5" />} label="Open TIA with upgrade" onClick={() => onOpenWorkbench(workbench, true)} />
                  <ContextMenuItem icon={<ShieldCheck className="h-3.5 w-3.5" />} label="Inspect TIA access" onClick={() => onInspectWorkbench(workbench)} />
                  <ContextMenuItem icon={<RefreshCw className="h-3.5 w-3.5" />} label="Refresh project tree" onClick={onRefresh} />
                  <ContextMenuSeparator />
                  <ContextMenuItem variant="error" icon={<Trash2 className="h-3.5 w-3.5" />} label="Delete this project" onClick={() => onDeleteWorkbench(workbench)} />
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
                          <ContextMenuTrigger>
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
                                <DropdownMenuTrigger render={<Button
                                    variant="ghost"
                                    size="icon-xs"
                                    aria-label={`Worktree actions ${worktree.name}`}
                                    onClick={event => event.stopPropagation()}
                                  >
                                    <Ellipsis className="h-3.5 w-3.5" />
                                  </Button>} />
                                <DropdownMenuContent align="end" className="w-max min-w-56">
                                  <MenuLabel title={worktree.name} />
                                  <DropdownMenuItem icon={<GitBranch className="h-3.5 w-3.5" />} label="Select worktree" onClick={() => onSelectWorktree(workbench, worktree)} />
                                  <DropdownMenuItem icon={<Plus className="h-3.5 w-3.5" />} label="New linked worktree" onClick={() => onCreateWorktree(workbench)} />
                                  <DropdownMenuItem icon={<Monitor className="h-3.5 w-3.5" />} label="Open TIA with UI" onClick={() => onOpenWorktree(workbench, worktree, false)} />
                                  <DropdownMenuItem icon={<RotateCw className="h-3.5 w-3.5" />} label="Open TIA with upgrade" onClick={() => onOpenWorktree(workbench, worktree, true)} />
                                  <DropdownMenuItem icon={<ShieldCheck className="h-3.5 w-3.5" />} label="Inspect TIA access" onClick={() => onInspectWorktree(workbench, worktree)} />
                                  <DropdownMenuItem icon={<Archive className="h-3.5 w-3.5" />} label="Archive TIA project" onClick={() => onArchiveWorktree(workbench, worktree)} />
                                  <DropdownMenuItem disabled={worktree.branch === 'master'} icon={<GitMerge className="h-3.5 w-3.5" />} label="Validate and merge to master" onClick={() => onMergeWorktree(workbench, worktree)} />
                                  <DropdownMenuSeparator />
                                  <DropdownMenuItem disabled={worktree.branch === 'master'} variant="error" icon={<Trash2 className="h-3.5 w-3.5" />} label="Remove worktree" onClick={() => onDeleteWorktree(workbench, worktree)} />
                                </DropdownMenuContent>
                              </DropdownMenu>
                            </div>
                          </ContextMenuTrigger>
                          <ContextMenuContent className="w-max min-w-56">
                            <MenuLabel title={worktree.name} />
                            <ContextMenuItem icon={<GitBranch className="h-3.5 w-3.5" />} label="Select worktree" onClick={() => onSelectWorktree(workbench, worktree)} />
                            <ContextMenuItem icon={<Plus className="h-3.5 w-3.5" />} label="New linked worktree" onClick={() => onCreateWorktree(workbench)} />
                            <ContextMenuItem icon={<Monitor className="h-3.5 w-3.5" />} label="Open TIA with UI" onClick={() => onOpenWorktree(workbench, worktree, false)} />
                            <ContextMenuItem icon={<RotateCw className="h-3.5 w-3.5" />} label="Open TIA with upgrade" onClick={() => onOpenWorktree(workbench, worktree, true)} />
                            <ContextMenuItem icon={<ShieldCheck className="h-3.5 w-3.5" />} label="Inspect TIA access" onClick={() => onInspectWorktree(workbench, worktree)} />
                            <ContextMenuItem icon={<Archive className="h-3.5 w-3.5" />} label="Archive TIA project" onClick={() => onArchiveWorktree(workbench, worktree)} />
                            <ContextMenuItem disabled={worktree.branch === 'master'} icon={<GitMerge className="h-3.5 w-3.5" />} label="Validate and merge to master" onClick={() => onMergeWorktree(workbench, worktree)} />
                            <ContextMenuSeparator />
                            <ContextMenuItem disabled={worktree.branch === 'master'} variant="error" icon={<Trash2 className="h-3.5 w-3.5" />} label="Remove worktree" onClick={() => onDeleteWorktree(workbench, worktree)} />
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
                                  <DropdownMenuTrigger render={<Button variant="ghost" size="icon-xs" aria-label={`Task actions ${task.title}`} className="absolute right-1 top-1/2 -translate-y-1/2 opacity-0 pointer-events-none group-hover:pointer-events-auto group-hover:opacity-100 focus-visible:pointer-events-auto focus-visible:opacity-100 data-[state=open]:pointer-events-auto data-[state=open]:opacity-100" onClick={event => event.stopPropagation()}>
                                      <Ellipsis className="h-3.5 w-3.5" />
                                    </Button>} />
                                  <DropdownMenuContent align="end" className="w-max min-w-56">
                                    <MenuLabel title={task.title} />
                                    <DropdownMenuSub>
                                      <DropdownMenuSubTrigger>Change status</DropdownMenuSubTrigger>
                                      <DropdownMenuContent className="w-max min-w-56">
                                        {(['todo', 'inProgress', 'done'] as const).map(status => <DropdownMenuItem key={status} label={status === 'inProgress' ? 'In progress' : status[0].toUpperCase() + status.slice(1)} onClick={() => onUpdateTask(workbench, worktree, task, { status })} />)}
                                      </DropdownMenuContent>
                                    </DropdownMenuSub>
                                    <DropdownMenuSub>
                                      <DropdownMenuSubTrigger>Change type and icon</DropdownMenuSubTrigger>
                                      <DropdownMenuContent className="w-max min-w-56">
                                        {(['issue', 'improvement', 'feature'] as const).map(type => {
                                          const TypeIcon = taskTypeIcon[type]
                                          return <DropdownMenuItem key={type} icon={<TypeIcon />} label={type[0].toUpperCase() + type.slice(1)} onClick={() => onUpdateTask(workbench, worktree, task, { type })} />
                                        })}
                                      </DropdownMenuContent>
                                    </DropdownMenuSub>
                                    <DropdownMenuSeparator />
                                    <DropdownMenuItem icon={<Pencil />} label="Rename task" onClick={() => openRenameTask(workbench, worktree, task)} />
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
                              <ContextMenuTrigger>
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
                              <ContextMenuContent className="w-max min-w-56">
                                <MenuLabel title="Hardware configuration" />
                                <ContextMenuItem icon={<CircuitBoard className="h-3.5 w-3.5" />} label="Select hardware configuration" onClick={() => onSelectHardware(workbench, worktree)} />
                                <ContextMenuSeparator />
                                <ContextMenuItem icon={<RotateCw className="h-3.5 w-3.5" />} label="Reload hardware configuration" onClick={() => onReloadHardware(workbench, worktree)} />
                                <ContextMenuItem icon={<RefreshCw className="h-3.5 w-3.5" />} label="Compare hardware with TIA" onClick={() => onCompareHardware(workbench, worktree)} />
                              </ContextMenuContent>
                            </ContextMenu>
                            {devices.length === 0 ? (
                              <div className="px-2 py-2 text-xs leading-4 text-muted-foreground">No registered PLC devices</div>
                            ) : devices.map(device => {
                              const selected = selection.deviceId === device.deviceId
                              const state = knowledgeState[device.deviceId] ?? 'missing'
                              return (
                                <ContextMenu key={device.deviceId}>
                                  <ContextMenuTrigger>
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
                                  <ContextMenuContent className="w-max min-w-56">
                                    <MenuLabel title={device.plcName} />
                                    <ContextMenuItem icon={<Cpu className="h-3.5 w-3.5" />} label="Select device" onClick={() => onSelectDevice(workbench, worktree, device.deviceId)} />
                                    <ContextMenuSeparator />
                                    <ContextMenuItem icon={<Monitor className="h-3.5 w-3.5" />} label="Open TIA with UI" onClick={() => onOpenDevice(workbench, worktree, device.deviceId, true)} />
                                    <ContextMenuItem icon={<MonitorOff className="h-3.5 w-3.5" />} label="Open TIA headless" onClick={() => onOpenDevice(workbench, worktree, device.deviceId, false)} />
                                    <ContextMenuItem icon={<RotateCw className="h-3.5 w-3.5" />} label="Open TIA with upgrade" onClick={() => onUpgradeDevice(workbench, worktree, device.deviceId)} />
                                    <ContextMenuItem icon={<ShieldCheck className="h-3.5 w-3.5" />} label="Inspect TIA access" onClick={() => onInspectDevice(workbench, worktree, device.deviceId)} />
                                    <ContextMenuItem icon={<RefreshCw className="h-3.5 w-3.5" />} label="Compare with TIA" onClick={() => onCompareDevice(workbench, worktree, device.deviceId)} />
                                    <ContextMenuItem icon={<RotateCw className="h-3.5 w-3.5" />} label="Rebuild project" onClick={() => onRebuildDevice(workbench, worktree, device.deviceId)} />
                                    <ContextMenuSeparator />
                                    <ContextMenuItem icon={<Database className="h-3.5 w-3.5" />} label="Update knowledge" onClick={() => onUpdateKnowledge(workbench, worktree, device.deviceId)} />
                                    <ContextMenuItem icon={<Database className="h-3.5 w-3.5" />} label="Rebuild knowledge" onClick={() => onRebuildKnowledge(workbench, worktree, device.deviceId)} />
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
