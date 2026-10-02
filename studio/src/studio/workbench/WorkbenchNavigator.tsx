import {
  Archive,
  ChevronDown,
  ChevronRight,
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
import { useState, type ReactNode } from 'react'
import type { DeviceSummary, EngineeringTask, EngineeringTaskTargetKind, TaskTarget, Workbench, WorkbenchRegistration, WorkbenchTagSearchResults, WorktreeTaskStatus } from '@/api/client'
import { taskTargetKind } from '@/api/client'
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
  /**
   * Which target below the selected worktree the shell shows: one of its PLC devices, or its hardware
   * configuration. Absent or null means the worktree itself is the deepest selected scope, which is
   * what the sections derive their visibility and their highlight from.
   */
  targetKind?: EngineeringTaskTargetKind | null
}

type TaskUpdate = { title: string; type: EngineeringTask['type']; status: WorktreeTaskStatus }

type Props = {
  workbenches: Workbench[]
  devicesByWorktree: Record<string, DeviceSummary[]>
  tasksByWorktree?: Record<string, EngineeringTask[]>
  activeTaskId?: string | null
  selection: WorkbenchSelection
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
  /** Creates a task already bound to the target the user created it from. */
  onAddTask?: (workbench: Workbench, worktree: WorkbenchRegistration, target: TaskTarget) => void
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
const knowledgeDotClass = (state: 'current' | 'stale' | 'missing' | 'failed') =>
  state === 'current' ? 'text-emerald-500'
    : state === 'stale' ? 'text-amber-500'
      : state === 'failed' ? 'text-red-500'
        : 'text-muted-foreground'

type NavigatorSectionProps = {
  /** Stable section identity, used for the header/body pairing and for test and style hooks. */
  id: string
  title: string
  /** Optional header control, for example the section's creation action. */
  action?: ReactNode
  children: ReactNode
}

/**
 * One collapsible navigator section.
 *
 * The header is a native button that owns this section's collapse state, so activating one header
 * changes only that section. The header sits outside the body's scroll region, so a scrolling
 * section can never carry a header out of view.
 */
function NavigatorSection({ id, title, action, children }: NavigatorSectionProps) {
  const [collapsed, setCollapsed] = useState(false)
  const bodyId = `navigator-section-${id}`
  return (
    <section data-navigator-section={id} className="flex min-h-0 flex-1 flex-col">
      <div className="flex shrink-0 items-center gap-1 px-1 pb-1 pt-1">
        <button
          type="button"
          aria-expanded={!collapsed}
          aria-controls={bodyId}
          onClick={() => setCollapsed(current => !current)}
          className="flex min-w-0 flex-1 items-center gap-1 rounded-sm text-left text-[9px] font-semibold tracking-[0.18em] text-muted-foreground transition-colors hover:text-foreground"
        >
          {collapsed
            ? <ChevronRight aria-hidden="true" className="h-3 w-3 shrink-0" />
            : <ChevronDown aria-hidden="true" className="h-3 w-3 shrink-0" />}
          <span className="truncate">{title}</span>
        </button>
        {action}
      </div>
      <div id={bodyId} hidden={collapsed} className="scrollbar-sleek min-h-0 flex-1 overflow-y-auto p-1">
        {children}
      </div>
    </section>
  )
}

type TaskRowProps = {
  workbench: Workbench
  worktree: WorkbenchRegistration
  task: EngineeringTask
  selected: boolean
  onSelect: (workbench: Workbench, worktree: WorkbenchRegistration, task: EngineeringTask) => void
  onUpdate: (workbench: Workbench, worktree: WorkbenchRegistration, task: EngineeringTask, update: Partial<TaskUpdate>) => void
  onRename: (workbench: Workbench, worktree: WorkbenchRegistration, task: EngineeringTask) => void
}

/**
 * One task row. The same row is used by the `TASKS` section and by the unbound-task group, so a task
 * never changes its affordances with the group it happens to appear in.
 */
function TaskRow({ workbench, worktree, task, selected, onSelect, onUpdate, onRename }: TaskRowProps) {
  const TaskIcon = taskTypeIcon[task.type]
  return (
    <div className="group relative">
      <button
        type="button"
        onClick={() => onSelect(workbench, worktree, task)}
        className={`relative flex min-h-8 w-full items-center gap-2 rounded-md border px-2 py-1 pr-8 text-left hover:bg-accent/40 ${selected ? 'border-ring/70' : 'border-transparent'}`}
        aria-label={`Open task ${task.title}`}
        aria-current={selected ? 'page' : undefined}
        data-task-selected={selected || undefined}
        data-task-type={task.type}
      >
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
              {(['todo', 'inProgress', 'done'] as const).map(status => <DropdownMenuItem key={status} onSelect={() => onUpdate(workbench, worktree, task, { status })}>{status === 'inProgress' ? 'In progress' : status[0].toUpperCase() + status.slice(1)}</DropdownMenuItem>)}
            </DropdownMenuSubContent>
          </DropdownMenuSub>
          <DropdownMenuSub>
            <DropdownMenuSubTrigger>Change type and icon</DropdownMenuSubTrigger>
            <DropdownMenuSubContent>
              {(['issue', 'improvement', 'feature'] as const).map(type => {
                const TypeIcon = taskTypeIcon[type]
                return <DropdownMenuItem key={type} onSelect={() => onUpdate(workbench, worktree, task, { type })}><TypeIcon />{type[0].toUpperCase() + type.slice(1)}</DropdownMenuItem>
              })}
            </DropdownMenuSubContent>
          </DropdownMenuSub>
          <DropdownMenuSeparator />
          <DropdownMenuItem onSelect={() => onRename(workbench, worktree, task)}><Pencil />Rename task</DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
    </div>
  )
}

export default function WorkbenchNavigator({
  workbenches,
  devicesByWorktree,
  tasksByWorktree = {},
  activeTaskId = null,
  selection,
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
  const [expandedWorktreeIds, setExpandedWorktreeIds] = useState<Set<string>>(
    () => new Set(selection.worktreeId ? [selection.worktreeId] : []),
  )
  const [clickedTaskId, setClickedTaskId] = useState<string | null>(null)
  const [renameTask, setRenameTask] = useState<{ workbench: Workbench; worktree: WorkbenchRegistration; task: EngineeringTask } | null>(null)
  const [renameTitle, setRenameTitle] = useState('')
  const matchingWorkbenchIds = new Set(filteredResults?.workbenches.map(result => result.entityId) ?? [])
  const matchingWorktrees = new Map(
    (filteredResults?.worktrees ?? []).map(result => [result.entityId, result]),
  )
  const visibleWorkbenches = filterActive
    ? workbenches.filter(workbench => matchingWorkbenchIds.has(workbench.workbenchId)
      || workbench.worktrees.some(worktree => matchingWorktrees.has(worktree.worktreeId)))
    : workbenches
  const selectedWorkbench = workbenches.find(workbench => workbench.workbenchId === selection.workbenchId) ?? null
  const showWorktreeSection = selectedWorkbench !== null || filterActive
  // The cascade derives the section from the selection one level above it; while the tag filter is
  // active the section is the server-owned worktree projection instead of one workbench's children.
  const worktreeRows: { workbench: Workbench; worktree: WorkbenchRegistration }[] = filterActive
    ? visibleWorkbenches.flatMap(workbench => workbench.worktrees
      .filter(worktree => matchingWorktrees.has(worktree.worktreeId))
      .map(worktree => ({ workbench, worktree })))
    : selectedWorkbench
      ? selectedWorkbench.worktrees.map(worktree => ({ workbench: selectedWorkbench, worktree }))
      : []
  const openRenameTask = (workbench: Workbench, worktree: WorkbenchRegistration, task: EngineeringTask) => {
    setRenameTitle(task.title)
    setRenameTask({ workbench, worktree, task })
  }
  const saveTaskRename = () => {
    if (!renameTask || !renameTitle.trim()) return
    onUpdateTask(renameTask.workbench, renameTask.worktree, renameTask.task, { title: renameTitle.trim() })
    setRenameTask(null)
  }

  // The cascade's tail: which target below the selected worktree is showing, and what it owns.
  const selectedWorktreeRow = worktreeRows.find(row => row.worktree.worktreeId === selection.worktreeId) ?? null
  const selectedWorktreeKey = selectedWorktreeRow
    ? worktreeKey(selectedWorktreeRow.workbench.workbenchId, selectedWorktreeRow.worktree.worktreeId)
    : null
  const selectedDevices = selectedWorktreeKey ? devicesByWorktree[selectedWorktreeKey] ?? [] : []
  const selectedTasks = selectedWorktreeKey ? tasksByWorktree[selectedWorktreeKey] ?? [] : []
  // A set deviceId always means that device is the target; the shell only has to name the target when
  // no device is selected, because a worktree whose hardware row is open still has a null deviceId.
  const selectedTargetKind: EngineeringTaskTargetKind | null =
    selection.deviceId ? 'device' : selection.targetKind === 'hardware' ? 'hardware' : null
  // A task with no device and no hardware kind predates the target model (AC-011 makes it uncreatable
  // now), which is exactly the "unbound" group AC-003 asks for and AC-010 keeps hardware tasks out of.
  const targetTasks = selectedTargetKind === 'hardware'
    ? selectedTasks.filter(task => taskTargetKind(task) === 'hardware')
    : selectedTargetKind === 'device'
      ? selectedTasks.filter(task => task.deviceId === selection.deviceId)
      : []
  const selectedTarget: TaskTarget | null = selectedWorktreeRow
    ? selectedTargetKind === 'hardware'
      ? { kind: 'hardware' }
      : selectedTargetKind === 'device' && selection.deviceId
        ? { kind: 'device', deviceId: selection.deviceId }
        : null
    : null

  const selectRowTask = (workbench: Workbench, worktree: WorkbenchRegistration, task: EngineeringTask) => {
    setClickedTaskId(task.taskId)
    onSelectTask(workbench, worktree, task)
  }

  /**
   * `DEVICE`: the selected worktree's PLC devices plus its single hardware target. The header action
   * is refresh only, and every operation the removed device subtree used to hold lives in the row's
   * own 3-dots menu, so no operation lost its entry point when that subtree went away.
   */
  const renderDeviceSection = () => {
    if (!selectedWorktreeRow) return null
    const { workbench, worktree } = selectedWorktreeRow
    const hardwareSelected = selectedTargetKind === 'hardware'
    return (
      <NavigatorSection
        id="device"
        title="DEVICE"
        action={(
          <Button variant="ghost" size="icon-xs" aria-label="Refresh devices" title="Refresh devices" onClick={onRefresh}>
            <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
          </Button>
        )}
      >
        <div
          className={`group mb-1 flex min-h-8 cursor-pointer items-center gap-2 rounded-sm px-1 py-1 ${hardwareSelected ? 'bg-accent' : 'hover:bg-accent/40'}`}
          aria-current={hardwareSelected ? 'true' : undefined}
          data-device-target="hardware"
          onClick={() => onSelectHardware(workbench, worktree)}
        >
          <CircuitBoard className={`h-4 w-4 ${hardwareSelected ? 'text-chart-2' : 'text-muted-foreground'}`} />
          <span className="min-w-0 flex-1 truncate text-xs">Hardware configuration</span>
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button
                variant="ghost"
                size="icon-xs"
                aria-label="Hardware configuration actions"
                onClick={event => event.stopPropagation()}
              >
                <Ellipsis className="h-3.5 w-3.5" />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end">
              <DropdownMenuLabel>Hardware configuration</DropdownMenuLabel>
              <DropdownMenuItem onSelect={() => onSelectHardware(workbench, worktree)}>
                <CircuitBoard className="h-3.5 w-3.5" />
                Select hardware configuration
              </DropdownMenuItem>
              <DropdownMenuSeparator />
              <DropdownMenuItem onSelect={() => onReloadHardware(workbench, worktree)}>
                <RotateCw className="h-3.5 w-3.5" />
                Reload hardware configuration
              </DropdownMenuItem>
              <DropdownMenuItem onSelect={() => onCompareHardware(workbench, worktree)}>
                <RefreshCw className="h-3.5 w-3.5" />
                Compare hardware with TIA
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        </div>
        {selectedDevices.length === 0 && (
          <div className="px-2 py-2 text-xs leading-4 text-muted-foreground">No registered PLC devices</div>
        )}
        {selectedDevices.map(device => {
          const selected = !hardwareSelected && selection.deviceId === device.deviceId
          const state = knowledgeState[device.deviceId] ?? 'missing'
          return (
            <div
              key={device.deviceId}
              className={`group mb-1 flex min-h-8 cursor-pointer items-center gap-2 rounded-sm px-1 py-1 ${selected ? 'bg-accent' : 'hover:bg-accent/40'}`}
              aria-current={selected ? 'true' : undefined}
              data-device-target={device.deviceId}
              onClick={() => onSelectDevice(workbench, worktree, device.deviceId)}
            >
              <Cpu className={`h-4 w-4 ${selected ? 'text-chart-2' : 'text-muted-foreground'}`} />
              <span className="min-w-0 flex-1 truncate text-xs">{device.plcName}</span>
              <Database className={`h-3 w-3 ${knowledgeDotClass(state)}`} />
              <DropdownMenu>
                <DropdownMenuTrigger asChild>
                  <Button
                    variant="ghost"
                    size="icon-xs"
                    aria-label={`Device actions ${device.plcName}`}
                    onClick={event => event.stopPropagation()}
                  >
                    <Ellipsis className="h-3.5 w-3.5" />
                  </Button>
                </DropdownMenuTrigger>
                <DropdownMenuContent align="end">
                  <DropdownMenuLabel>{device.plcName}</DropdownMenuLabel>
                  <DropdownMenuItem onSelect={() => onSelectDevice(workbench, worktree, device.deviceId)}>
                    <Cpu className="h-3.5 w-3.5" />
                    Select device
                  </DropdownMenuItem>
                  <DropdownMenuSeparator />
                  <DropdownMenuItem onSelect={() => onOpenDevice(workbench, worktree, device.deviceId, true)}>
                    <Monitor className="h-3.5 w-3.5" />
                    Open TIA with UI
                  </DropdownMenuItem>
                  <DropdownMenuItem onSelect={() => onOpenDevice(workbench, worktree, device.deviceId, false)}>
                    <MonitorOff className="h-3.5 w-3.5" />
                    Open TIA headless
                  </DropdownMenuItem>
                  <DropdownMenuItem onSelect={() => onUpgradeDevice(workbench, worktree, device.deviceId)}>
                    <RotateCw className="h-3.5 w-3.5" />
                    Open TIA with upgrade
                  </DropdownMenuItem>
                  <DropdownMenuItem onSelect={() => onInspectDevice(workbench, worktree, device.deviceId)}>
                    <ShieldCheck className="h-3.5 w-3.5" />
                    Inspect TIA access
                  </DropdownMenuItem>
                  <DropdownMenuItem onSelect={() => onCompareDevice(workbench, worktree, device.deviceId)}>
                    <RefreshCw className="h-3.5 w-3.5" />
                    Compare with TIA
                  </DropdownMenuItem>
                  <DropdownMenuItem onSelect={() => onRebuildDevice(workbench, worktree, device.deviceId)}>
                    <RotateCw className="h-3.5 w-3.5" />
                    Rebuild project
                  </DropdownMenuItem>
                  <DropdownMenuSeparator />
                  <DropdownMenuItem onSelect={() => onUpdateKnowledge(workbench, worktree, device.deviceId)}>
                    <Database className="h-3.5 w-3.5" />
                    Update knowledge
                  </DropdownMenuItem>
                  <DropdownMenuItem onSelect={() => onRebuildKnowledge(workbench, worktree, device.deviceId)}>
                    <Database className="h-3.5 w-3.5" />
                    Rebuild knowledge
                  </DropdownMenuItem>
                </DropdownMenuContent>
              </DropdownMenu>
            </div>
          )
        })}
      </NavigatorSection>
    )
  }

  /**
   * `TASKS`: what the selected target owns. It exists only once a target is selected, and its header
   * creates a task already bound to that target, so creating from the hardware row cannot silently
   * fall back to a PLC device.
   */
  const renderTasksSection = () => {
    if (!selectedWorktreeRow || !selectedTarget) return null
    const { workbench, worktree } = selectedWorktreeRow
    const targetLabel = selectedTarget.kind === 'hardware'
      ? 'hardware configuration'
      : selectedDevices.find(device => device.deviceId === selectedTarget.deviceId)?.plcName ?? selectedTarget.deviceId
    const addTask = () => onAddTask(workbench, worktree, selectedTarget)
    const addButton = (
      <Button variant="ghost" size="xs" className="text-muted-foreground hover:text-foreground" onClick={addTask}>
        <Plus className="h-3 w-3" /> Add task
      </Button>
    )
    return (
      <NavigatorSection
        id="tasks"
        title="TASKS"
        action={(
          <Button
            variant="ghost"
            size="icon-xs"
            aria-label={`Create task for ${targetLabel}`}
            title="Create task"
            onClick={addTask}
          >
            <Plus className="h-3.5 w-3.5" />
          </Button>
        )}
      >
        {targetTasks.length === 0 ? addButton : (
          <>
            {targetTasks.map(task => (
              <TaskRow
                key={task.taskId}
                workbench={workbench}
                worktree={worktree}
                task={task}
                selected={(activeTaskId ?? clickedTaskId) === task.taskId}
                onSelect={selectRowTask}
                onUpdate={onUpdateTask}
                onRename={openRenameTask}
              />
            ))}
            {addButton}
          </>
        )}
      </NavigatorSection>
    )
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
      </div>
      {filterControl}

      <div className="flex min-h-0 flex-1 flex-col gap-2 overflow-hidden p-2">
        <NavigatorSection
          id="projects"
          title="PROJECTS"
          action={(
            <Button variant="ghost" size="icon-xs" aria-label="Create workbench" title="Create workbench" onClick={onCreateWorkbench}>
              <Plus className="h-3.5 w-3.5" />
            </Button>
          )}
        >
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
            return (
              <ContextMenu key={workbench.workbenchId}>
                <ContextMenuTrigger asChild>
                  <div
                    className={`group mb-1 flex min-h-8 cursor-pointer items-center gap-2 rounded-sm px-1 py-1 ${workbenchSelected ? 'bg-accent/50' : 'hover:bg-accent/40'}`}
                    aria-current={workbenchSelected ? 'true' : undefined}
                    onClick={() => onSelectWorkbench(workbench)}
                  >
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
            )
          })}
        </NavigatorSection>

        {showWorktreeSection && (
          <NavigatorSection
            id="worktree"
            title="WORKTREE"
            action={!filterActive && selectedWorkbench ? (
              <Button
                variant="ghost"
                size="icon-xs"
                aria-label={`Create worktree for ${selectedWorkbench.name}`}
                title="Create worktree"
                onClick={() => onCreateWorktree(selectedWorkbench)}
              >
                <Plus className="h-3.5 w-3.5" />
              </Button>
            ) : undefined}
          >
            {worktreeRows.map(({ workbench, worktree }) => {
              const worktreeSelected = selection.worktreeId === worktree.worktreeId
              const available = matchingWorktrees.get(worktree.worktreeId)?.available ?? true
              const key = worktreeKey(workbench.workbenchId, worktree.worktreeId)
              // The unbound group is per worktree: it exists only for the selected one and only while
              // it actually holds a task that resolves to no target.
              const rowUnboundTasks = (tasksByWorktree[key] ?? [])
                .filter(task => !task.deviceId && taskTargetKind(task) === 'device')
              return (
                <div key={key}>
                  <ContextMenu>
                    <ContextMenuTrigger asChild>
                        <div
                          className={`group flex min-h-8 cursor-pointer items-center gap-2 rounded-sm px-1 py-1 ${
                          worktreeSelected && selectedTargetKind === null
                            ? 'bg-accent'
                            : worktreeSelected
                              ? 'bg-accent/70'
                              : 'hover:bg-accent/40'
                        }`}
                          aria-current={worktreeSelected ? 'true' : undefined}
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
                  {/* The unbound group: the selected worktree's tasks that resolve to no target. It
                      carries no creation action, because a targetless task stays rejected (AC-011). */}
                  {!filterActive && worktreeSelected && expandedWorktreeIds.has(worktree.worktreeId) && rowUnboundTasks.length > 0 && (
                    <div className="ml-4 border-l py-0.5 pl-2" style={{ borderColor: 'var(--border)' }} data-unbound-tasks>
                      <div className="px-2 pb-1 text-[9px] font-semibold tracking-[0.18em] text-muted-foreground">NO TARGET</div>
                      {rowUnboundTasks.map(task => (
                        <TaskRow
                          key={task.taskId}
                          workbench={workbench}
                          worktree={worktree}
                          task={task}
                          selected={(activeTaskId ?? clickedTaskId) === task.taskId}
                          onSelect={(selectedWorkbench, selectedWorktree, selectedTask) => {
                            setClickedTaskId(selectedTask.taskId)
                            onSelectTask(selectedWorkbench, selectedWorktree, selectedTask)
                          }}
                          onUpdate={onUpdateTask}
                          onRename={openRenameTask}
                        />
                      ))}
                    </div>
                  )}
                </div>
              )
            })}
          </NavigatorSection>
        )}

        {!filterActive && renderDeviceSection()}

        {!filterActive && renderTasksSection()}
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
