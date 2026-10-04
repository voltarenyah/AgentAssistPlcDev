import { useEffect, useMemo, useRef, useState } from 'react'
import { Check, ChevronDown, LayoutGrid, List, ListTodo, Loader2, Pencil, Plus, Trash2, X } from 'lucide-react'
import * as api from '@/api/client'
import { Button } from '@/components/ui/button'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import { showErrorToast } from '@/components/ui/toast'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import TaskCreateDialog from './TaskCreateDialog'
import WorktreeTaskCard, { type TaskCardModel } from './WorktreeTaskCard'
import WorktreeTaskListItem, { type TaskListItemModel } from './WorktreeTaskListItem'

export type TaskViewMode = 'cards' | 'list'

type Props = {
  workbenchId: string
  worktreeId: string
  tasks: TaskSurface[]
  loading: boolean
  error: string | null
  onChanged: () => void
  deviceIds?: string[]
  projectTasks?: api.EngineeringTask[]
  onOpenTaskDetail?: (task: api.EngineeringTask) => void
  onStartChat?: (task: TaskSurface) => void
  onOpenInTia?: (task: TaskSurface) => void
  onOpenTaskSession?: (task: api.EngineeringTask, sessionId: string) => void
  viewMode: TaskViewMode
  onViewModeChange: (mode: TaskViewMode) => void
}

const displayError = (error: unknown) => {
  if (error instanceof api.WorkbenchApiError) return `${error.code}: ${error.message}`
  return error instanceof Error ? error.message : 'Unexpected operation failure'
}

const taskStatusLabel = (status: string) =>
  status === 'inProgress' ? 'In Progress' : status === 'done' ? 'Done' : status === 'todo' ? 'Todo' : status

const taskStatusOrder: api.WorktreeTaskStatus[] = ['todo', 'inProgress', 'done']
const EMPTY_PROJECT_TASKS: api.EngineeringTask[] = []
const TASK_LIST_COLUMN_MIN_WIDTHS = [120, 92, 88, 120, 84, 116]
const TASK_LIST_COLUMNS = [
  { label: 'Name', align: 'left' },
  { label: 'Status', align: 'left' },
  { label: 'Type', align: 'left' },
  { label: 'PLC', align: 'left' },
  { label: 'Sessions', align: 'center' },
  { label: 'Actions', align: 'right' },
] as const

const measureTaskListColumns = (table: HTMLTableElement) =>
  Array.from(table.querySelectorAll('col')).map(column => column.getBoundingClientRect().width)

type TaskSurface = api.WorktreeTask | api.EngineeringTask
const isLegacyTask = (task: TaskSurface): task is api.WorktreeTask => 'elementRefs' in task

const taskTypeLabel = (type?: TaskSurface['type']) =>
  type === 'issue' ? 'Issue' : type === 'improvement' ? 'Improvement' : 'Feature'

const taskCardModel = (task: TaskSurface, devices: api.DeviceSummary[], sessions: api.ChatSessionInfo[] = []): TaskCardModel => ({
  title: task.title,
  status: taskStatusLabel(task.status),
  scope: task.scope === 'project' ? 'Project' : 'Worktree',
  type: taskTypeLabel(task.type),
  deviceName: !isLegacyTask(task) && task.deviceId
    ? devices.find(device => device.deviceId === task.deviceId)?.plcName || task.deviceId
    : undefined,
  sessions,
})

const taskListItemModel = (task: TaskSurface, devices: api.DeviceSummary[], sessionsCount: number): TaskListItemModel => {
  const { title, status, type, deviceName } = taskCardModel(task, devices)
  return { title, status, type, deviceName, sessionsCount }
}

export function ActiveTaskSelector({
  tasks,
  activeTask,
  onChange,
  worktreeId,
}: {
  tasks: TaskSurface[]
  activeTask: api.EngineeringTask | null
  onChange: (taskId: string | null) => Promise<void>
  worktreeId?: string
}) {
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const compatible = tasks.filter(task => task.scope !== 'worktree' || !task.worktreeId || task.worktreeId === worktreeId)
  const selectedId = activeTask?.taskId ?? ''
  const select = async (taskId: string) => {
    setSaving(true)
    try {
      await onChange(taskId || null)
      setError(null)
    } catch (cause) {
      setError(displayError(cause))
    } finally {
      setSaving(false)
    }
  }
  return (
    <div className="rounded-lg border p-3" style={{ borderColor: 'var(--border)' }}>
      <label className="field-label" htmlFor="active-task-selector"><span>Active task</span></label>
      <div className="mt-1 flex items-center gap-2">
        <select
          id="active-task-selector"
          aria-label="Active task"
          className="field-input h-8 min-w-0 flex-1 text-[10px]"
          value={selectedId}
          disabled={saving}
          onChange={event => { void select(event.target.value) }}
        >
          <option value="">No active task</option>
          {compatible.map(task => (
            <option key={task.taskId} value={task.taskId}>
              {task.title} ({task.scope === 'project' ? 'Project' : 'Worktree'} · {taskTypeLabel(task.type)})
            </option>
          ))}
        </select>
        <button type="button" className="secondary-button h-8 text-[9px]" disabled={!activeTask || saving} onClick={() => { void select('') }} onKeyDown={event => {
          if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault()
            void select('')
          }
        }}>
          Clear
        </button>
      </div>
      <div className="mt-1 text-[9px] text-muted-foreground">
        {activeTask ? <><span className="font-medium text-foreground">{activeTask.title}</span> · {activeTask.scope === 'project' ? 'Project' : 'Worktree'} scope · {taskTypeLabel(activeTask.type)}</> : 'New sessions and actions remain unassigned until you choose a task.'}
      </div>
      {error && <div role="alert" className="mt-2 flex items-center justify-between gap-2 text-[9px] text-destructive"><span>Active task could not be changed: {error}</span><button type="button" className="underline" onClick={() => setError(null)}>Dismiss</button></div>}
    </div>
  )
}

const taskStatusClasses = (status: api.WorktreeTaskStatus) =>
  status === 'done'
    ? 'border-border bg-muted text-muted-foreground'
    : status === 'inProgress'
      ? 'border-amber-500/30 bg-amber-500/10 text-amber-600 dark:text-amber-400'
      : 'border-chart-2/30 bg-chart-2/10 text-chart-2'

function TaskStatusControl({ task, onChange }: {
  task: api.WorktreeTask
  onChange: (status: api.WorktreeTaskStatus) => void
}) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button
          type="button"
          aria-label={`Change status of ${task.title}`}
          className={`inline-flex shrink-0 items-center gap-1 rounded-full border px-2 py-0.5 text-[8px] font-medium uppercase tracking-[0.1em] ${taskStatusClasses(task.status)}`}
        >
          {taskStatusLabel(task.status)}
          <ChevronDown className="h-2.5 w-2.5" />
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent>
        {taskStatusOrder.map(option => (
          <DropdownMenuItem key={option} onSelect={() => onChange(option)}>
            <Check className={`h-3.5 w-3.5 ${option === task.status ? 'opacity-100' : 'opacity-0'}`} />
            {taskStatusLabel(option)}
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

type EditDraft = {
  task: api.WorktreeTask
  title: string
  details: string
  elementRefs: string[]
}

export default function WorktreeTasksPanel({ workbenchId, worktreeId, tasks, loading, error, onChanged, deviceIds = [], projectTasks = EMPTY_PROJECT_TASKS, onOpenTaskDetail, onStartChat, onOpenInTia, onOpenTaskSession, viewMode, onViewModeChange }: Props) {
  const [createOpen, setCreateOpen] = useState(false)
  const [devices, setDevices] = useState<api.DeviceSummary[]>([])
  const [sessionsByDevice, setSessionsByDevice] = useState<Record<string, api.ChatSessionInfo[]>>({})
  const [taskListColumnWidths, setTaskListColumnWidths] = useState<number[] | null>(null)
  const activeColumnResizeCleanup = useRef<(() => void) | null>(null)
  const [draft, setDraft] = useState<EditDraft | null>(null)
  const [newRef, setNewRef] = useState('')
  const [savingEdit, setSavingEdit] = useState(false)
  const visibleTasks = [...projectTasks, ...tasks.filter(task => task.scope !== 'worktree' || task.worktreeId === worktreeId)]
  const sessionDeviceIds = useMemo(() => [...new Set(visibleTasks.flatMap(task =>
    !isLegacyTask(task) && task.deviceId ? [task.deviceId] : [],
  ))], [tasks, projectTasks, worktreeId])
  const availableDevices = devices.length > 0
    ? devices
    : deviceIds.map((deviceId, index) => ({ deviceId, plcName: `Device ${index + 1}` }))

  const startTaskListColumnResize = (columnIndex: number, event: React.PointerEvent<HTMLDivElement>) => {
    if (event.button !== 0) return
    event.preventDefault()
    event.stopPropagation()
    activeColumnResizeCleanup.current?.()
    const table = event.currentTarget.closest('table')
    if (!table) return

    const widths = taskListColumnWidths ?? measureTaskListColumns(table)
    if (widths.length !== TASK_LIST_COLUMNS.length || widths.some(width => width <= 0)) return
    const startX = event.clientX
    const body = document.body
    const previousCursor = body.style.cursor
    const previousUserSelect = body.style.userSelect
    body.style.cursor = 'col-resize'
    body.style.userSelect = 'none'

    const cleanup = () => {
      window.removeEventListener('pointermove', onPointerMove)
      window.removeEventListener('pointerup', onPointerUp)
      window.removeEventListener('pointercancel', onPointerUp)
      body.style.cursor = previousCursor
      body.style.userSelect = previousUserSelect
      if (activeColumnResizeCleanup.current === cleanup) activeColumnResizeCleanup.current = null
    }
    const onPointerMove = (moveEvent: PointerEvent) => {
      if (moveEvent.pointerId !== event.pointerId) return
      const delta = moveEvent.clientX - startX
      const lowerBound = TASK_LIST_COLUMN_MIN_WIDTHS[columnIndex] - widths[columnIndex]
      const upperBound = widths[columnIndex + 1] - TASK_LIST_COLUMN_MIN_WIDTHS[columnIndex + 1]
      const appliedDelta = Math.max(lowerBound, Math.min(upperBound, delta))
      const nextWidths = [...widths]
      nextWidths[columnIndex] += appliedDelta
      nextWidths[columnIndex + 1] -= appliedDelta
      setTaskListColumnWidths(nextWidths)
    }
    const onPointerUp = (upEvent: PointerEvent) => {
      if (upEvent.pointerId !== event.pointerId) return
      cleanup()
    }
    activeColumnResizeCleanup.current = cleanup
    window.addEventListener('pointermove', onPointerMove)
    window.addEventListener('pointerup', onPointerUp)
    window.addEventListener('pointercancel', onPointerUp)
  }

  const resizeTaskListColumnWithKeyboard = (columnIndex: number, event: React.KeyboardEvent<HTMLDivElement>) => {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return
    event.preventDefault()
    const table = event.currentTarget.closest('table')
    if (!table) return
    const widths = taskListColumnWidths ?? measureTaskListColumns(table)
    if (widths.length !== TASK_LIST_COLUMNS.length || widths.some(width => width <= 0)) return
    const direction = event.key === 'ArrowRight' ? 1 : -1
    const lowerBound = TASK_LIST_COLUMN_MIN_WIDTHS[columnIndex] - widths[columnIndex]
    const upperBound = widths[columnIndex + 1] - TASK_LIST_COLUMN_MIN_WIDTHS[columnIndex + 1]
    const appliedDelta = Math.max(lowerBound, Math.min(upperBound, direction * 8))
    const nextWidths = [...widths]
    nextWidths[columnIndex] += appliedDelta
    nextWidths[columnIndex + 1] -= appliedDelta
    setTaskListColumnWidths(nextWidths)
  }

  useEffect(() => () => activeColumnResizeCleanup.current?.(), [])

  useEffect(() => {
    let cancelled = false
    void api.listDevices(workbenchId, worktreeId)
      .then(result => { if (!cancelled) setDevices(result) })
      .catch(() => { if (!cancelled) setDevices([]) })
    return () => { cancelled = true }
  }, [workbenchId, worktreeId])

  useEffect(() => {
    let cancelled = false
    void Promise.all(sessionDeviceIds.map(async deviceId => [
      deviceId,
      await api.listDeviceSessions(workbenchId, worktreeId, deviceId).catch(() => []),
    ] as const)).then(entries => {
      if (!cancelled) setSessionsByDevice(Object.fromEntries(entries))
    })
    return () => { cancelled = true }
  }, [workbenchId, worktreeId, sessionDeviceIds])

  const mutate = (action: () => Promise<unknown>) => {
    void action()
      .then(onChanged)
      .catch(mutationError => showErrorToast(`Task could not be updated: ${displayError(mutationError)}`))
  }

  const openEdit = (task: api.WorktreeTask) => {
    setDraft({ task, title: task.title, details: task.details ?? '', elementRefs: [...task.elementRefs] })
    setNewRef('')
  }

  const saveEdit = () => {
    if (!draft || savingEdit) return
    const title = draft.title.trim()
    if (!title) return
    setSavingEdit(true)
    void api.updateWorktreeTask(workbenchId, worktreeId, draft.task.taskId, {
      title,
      details: draft.details.trim() || null,
      elementRefs: draft.elementRefs,
    })
      .then(() => {
        setDraft(null)
        onChanged()
      })
      .catch(saveError => showErrorToast(`Task could not be saved: ${displayError(saveError)}`))
      .finally(() => setSavingEdit(false))
  }

  const addRef = () => {
    const value = newRef.trim()
    if (!value || !draft) return
    if (!draft.elementRefs.includes(value)) {
      setDraft({ ...draft, elementRefs: [...draft.elementRefs, value] })
    }
    setNewRef('')
  }

  const renderTask = (task: TaskSurface) => {
    const legacy = isLegacyTask(task)
    const statusControl = legacy ? <TaskStatusControl
      task={task}
      onChange={next => mutate(() => api.updateWorktreeTask(workbenchId, worktreeId, task.taskId, { status: next }))}
    /> : undefined
    const hasActions = Boolean(onStartChat || onOpenInTia && !legacy || onOpenTaskDetail && !legacy || legacy)
    const actions = hasActions ? <>
      {onStartChat && <Button type="button" variant="secondary" size="xs" aria-label={`New chat for ${task.title}`} onClick={() => onStartChat(task)}>New chat</Button>}
      {onOpenInTia && !legacy && <Button type="button" variant="outline" size="xs" aria-label={`Open ${task.title} in TIA Portal`} onClick={() => onOpenInTia(task)}>Open in TIA</Button>}
      {onOpenTaskDetail && !legacy && <Button type="button" variant="outline" size="xs" aria-label={`Open task detail ${task.title}`} onClick={() => onOpenTaskDetail(task)}>Detail</Button>}
      {legacy && <><Button type="button" variant="ghost" size="icon-xs" aria-label={`Edit task ${task.title}`} onClick={() => openEdit(task)}><Pencil /></Button>
        <Button type="button" variant="ghost" size="icon-xs" aria-label={`Delete task ${task.title}`} onClick={() => {
          if (window.confirm(`Delete task "${task.title}"?`)) mutate(() => api.deleteWorktreeTask(workbenchId, worktreeId, task.taskId))
        }}><Trash2 /></Button></>}
    </> : undefined

    const taskSessions = !legacy && task.deviceId
      ? (sessionsByDevice[task.deviceId] ?? []).filter(session => session.taskId === task.taskId)
      : []

    return viewMode === 'cards'
      ? <WorktreeTaskCard key={task.taskId} model={taskCardModel(task, availableDevices, taskSessions)} statusControl={statusControl} actions={actions} onOpenSession={!legacy && onOpenTaskSession ? sessionId => onOpenTaskSession(task, sessionId) : undefined} />
      : <WorktreeTaskListItem key={task.taskId} model={taskListItemModel(task, availableDevices, taskSessions.length)} statusControl={statusControl} actions={actions} />
  }

  if (loading) {
    return (
      <div className="flex items-center justify-center gap-2 p-10 text-[10px] text-muted-foreground">
        <Loader2 className="h-4 w-4 animate-spin" /> Loading tasks...
      </div>
    )
  }

  if (error) {
    return <div className="p-10 text-center text-[10px] text-muted-foreground">Tasks could not be loaded: {error}</div>
  }

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <ToggleGroup type="single" value={viewMode} variant="outline" size="sm" aria-label="Task display mode" onValueChange={value => {
          if (value === 'cards' || value === 'list') onViewModeChange(value)
        }}>
          <ToggleGroupItem value="cards" aria-label="Card view" className="text-xs"><LayoutGrid className="h-3.5 w-3.5" /> Cards</ToggleGroupItem>
          <ToggleGroupItem value="list" aria-label="List view" className="text-xs"><List className="h-3.5 w-3.5" /> List</ToggleGroupItem>
        </ToggleGroup>
        <Button type="button" size="sm" onClick={() => setCreateOpen(true)}>
          <Plus className="h-3.5 w-3.5" /> Add task
        </Button>
      </div>

      {visibleTasks.length === 0 ? (
        <div className="grid place-items-center rounded-xl border border-dashed p-10 text-center" style={{ borderColor: 'var(--border)' }}>
          <ListTodo className="mb-3 h-6 w-6 text-chart-2" />
          <p className="text-[10px] text-muted-foreground">
            No tasks yet. Add the first modification task for this worktree above.
          </p>
        </div>
      ) : (
        viewMode === 'cards'
          ? <div className="grid items-start gap-3 [grid-template-columns:repeat(auto-fill,minmax(min(100%,22rem),1fr))]">{visibleTasks.map(renderTask)}</div>
          : <div className="overflow-x-auto rounded-xl border bg-card">
            <table className="w-full min-w-[680px] table-fixed border-collapse text-xs" style={taskListColumnWidths ? { width: `${taskListColumnWidths.reduce((sum, width) => sum + width, 0)}px` } : undefined}>
              <colgroup>{TASK_LIST_COLUMNS.map((column, index) => <col key={column.label} className={taskListColumnWidths ? undefined : ['w-[27%]', 'w-[13%]', 'w-[13%]', 'w-[19%]', 'w-[10%]', 'w-[18%]'][index]} style={taskListColumnWidths ? { width: `${taskListColumnWidths[index]}px` } : undefined} />)}</colgroup>
              <thead className="bg-muted/40 text-left text-muted-foreground">
                <tr className="border-b">
                  {TASK_LIST_COLUMNS.map((column, index) => <th key={column.label} scope="col" className={`relative px-2 py-2 font-medium ${column.align === 'center' ? 'text-center' : column.align === 'right' ? 'text-right' : 'text-left'}`}>
                    {column.label}
                    {index < TASK_LIST_COLUMNS.length - 1 && <div role="separator" aria-orientation="vertical" aria-label={`Resize ${column.label} column`} tabIndex={0} className="absolute right-0 top-0 z-10 h-full w-2 translate-x-1/2 cursor-col-resize touch-none after:absolute after:inset-y-1 after:left-1/2 after:w-px after:bg-border hover:bg-primary/10 hover:after:bg-primary focus-visible:outline-none focus-visible:after:bg-primary" onPointerDown={event => startTaskListColumnResize(index, event)} onKeyDown={event => resizeTaskListColumnWithKeyboard(index, event)} />}
                  </th>)}
                </tr>
              </thead>
              <tbody>{visibleTasks.map(renderTask)}</tbody>
            </table>
          </div>
      )}

      <Dialog open={draft !== null} onOpenChange={open => { if (!open) setDraft(null) }}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Edit task</DialogTitle>
            <DialogDescription>Title, modification plan (markdown), and linked PLC elements.</DialogDescription>
          </DialogHeader>
          {draft && (
            <div className="space-y-3">
              <label className="field-label">
                <span>Title</span>
                <input
                  aria-label="Task title"
                  className="field-input"
                  value={draft.title}
                  onChange={event => setDraft({ ...draft, title: event.target.value })}
                />
              </label>
              <label className="field-label">
                <span>Details / modification plan (markdown)</span>
                <textarea
                  aria-label="Task details"
                  className="field-input min-h-[120px] resize-y py-1.5 font-mono text-[10px]"
                  value={draft.details}
                  onChange={event => setDraft({ ...draft, details: event.target.value })}
                />
              </label>
              <div className="field-label">
                <span>PLC element references</span>
                <div className="mt-1 flex flex-wrap items-center gap-1">
                  {draft.elementRefs.map(elementRef => (
                    <span key={elementRef} className="inline-flex items-center gap-1 rounded bg-muted px-1.5 py-0.5 font-mono text-[9px]">
                      {elementRef}
                      <button
                        type="button"
                        aria-label={`Remove reference ${elementRef}`}
                        className="text-muted-foreground hover:text-foreground"
                        onClick={() => setDraft({ ...draft, elementRefs: draft.elementRefs.filter(value => value !== elementRef) })}
                      >
                        <X className="h-2.5 w-2.5" />
                      </button>
                    </span>
                  ))}
                  <input
                    aria-label="Add element reference"
                    className="field-input h-6 w-44 text-[9px]"
                    placeholder="Device01/FB_Motor_Control"
                    value={newRef}
                    onChange={event => setNewRef(event.target.value)}
                    onKeyDown={event => {
                      if (event.key === 'Enter') {
                        event.preventDefault()
                        addRef()
                      }
                    }}
                  />
                </div>
              </div>
            </div>
          )}
          <DialogFooter>
            <button className="secondary-button" onClick={() => setDraft(null)} disabled={savingEdit}>Cancel</button>
            <button className="primary-button" onClick={saveEdit} disabled={!draft?.title.trim() || savingEdit}>
              {savingEdit && <Loader2 className="h-3.5 w-3.5 animate-spin" />} Save task
            </button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
      {/* This panel's own action defaults to a PLC device; the navigator's action preselects whatever
          target it was invoked from instead, through the shell's own instance of the same dialog. */}
      <TaskCreateDialog
        workbenchId={workbenchId}
        worktreeId={worktreeId}
        open={createOpen}
        origin={{ kind: 'device', deviceId: deviceIds[0] ?? '' }}
        devices={availableDevices}
        onClose={() => setCreateOpen(false)}
        onCreated={onChanged}
      />
    </div>
  )
}
