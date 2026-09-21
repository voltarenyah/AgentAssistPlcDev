import { useEffect, useState } from 'react'
import { Check, ChevronDown, ListTodo, Loader2, Pencil, Plus, Trash2, X } from 'lucide-react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import * as api from '@/api/client'
import { showErrorToast } from '@/components/ui/toast'
import {
  Button,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
  Input,
  Textarea,
} from '@notion-kit/ui/primitives'

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
  openCreate?: boolean
  onCreateClosed?: () => void
}

const displayError = (error: unknown) => {
  if (error instanceof api.WorkbenchApiError) return `${error.code}: ${error.message}`
  return error instanceof Error ? error.message : 'Unexpected operation failure'
}

const taskStatusLabel = (status: api.WorktreeTaskStatus) =>
  status === 'inProgress' ? 'In Progress' : status === 'done' ? 'Done' : 'Todo'

const taskStatusOrder: api.WorktreeTaskStatus[] = ['todo', 'inProgress', 'done']

type TaskSurface = api.WorktreeTask | api.EngineeringTask
const isLegacyTask = (task: TaskSurface): task is api.WorktreeTask => 'elementRefs' in task

const taskTypeLabel = (type?: TaskSurface['type']) =>
  type === 'issue' ? 'Issue' : type === 'improvement' ? 'Improvement' : 'Feature'

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
    ? 'border-border bg-surface-muted text-muted-foreground'
    : status === 'inProgress'
      ? 'border-amber-500/30 bg-amber-500/10 text-amber-600 dark:text-amber-400'
      : 'border-chart-2/30 bg-chart-2/10 text-chart-2'

function TaskStatusControl({ task, onChange }: {
  task: api.WorktreeTask
  onChange: (status: api.WorktreeTaskStatus) => void
}) {
  return (
    <DropdownMenu>
      {/*
        notion-kit has no `asChild`; its trigger renders a real <button>, so the
        trigger's own props live on DropdownMenuTrigger. Use `render={<X/>}`
        only when composing a custom component.
      */}
      <DropdownMenuTrigger
        type="button"
        aria-label={`Change status of ${task.title}`}
        className={`inline-flex shrink-0 cursor-pointer items-center gap-1 rounded-full border px-2 py-0.5 text-[8px] font-medium uppercase tracking-[0.1em] ${taskStatusClasses(task.status)}`}
      >
        {taskStatusLabel(task.status)}
        <ChevronDown className="h-2.5 w-2.5" />
      </DropdownMenuTrigger>
      <DropdownMenuContent>
        {taskStatusOrder.map(option => (
          <DropdownMenuItem key={option} onClick={() => onChange(option)}>
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

export default function WorktreeTasksPanel({ workbenchId, worktreeId, tasks, loading, error, onChanged, deviceIds = [], projectTasks = [], onOpenTaskDetail, onStartChat, openCreate = false, onCreateClosed }: Props) {
  const [createOpen, setCreateOpen] = useState(false)
  const [newTitle, setNewTitle] = useState('')
  const [newType, setNewType] = useState<api.EngineeringTask['type']>('feature')
  const [newDevice, setNewDevice] = useState('')
  const [devices, setDevices] = useState<api.DeviceSummary[]>([])
  const [adding, setAdding] = useState(false)
  const [draft, setDraft] = useState<EditDraft | null>(null)
  const [newRef, setNewRef] = useState('')
  const [savingEdit, setSavingEdit] = useState(false)
  const visibleTasks = [...projectTasks, ...tasks]
  const availableDevices = devices.length > 0
    ? devices
    : deviceIds.map((deviceId, index) => ({ deviceId, plcName: `Device ${index + 1}` }))

  useEffect(() => {
    let cancelled = false
    void api.listDevices(workbenchId, worktreeId)
      .then(result => { if (!cancelled) setDevices(result) })
      .catch(() => { if (!cancelled) setDevices([]) })
    return () => { cancelled = true }
  }, [workbenchId, worktreeId])

  useEffect(() => {
    if (openCreate) setCreateOpen(true)
  }, [openCreate])

  const mutate = (action: () => Promise<unknown>) => {
    void action()
      .then(onChanged)
      .catch(mutationError => showErrorToast(`Task could not be updated: ${displayError(mutationError)}`))
  }

  const addTask = () => {
    const title = newTitle.trim()
    if (!title || !newDevice || adding) return
    setAdding(true)
    void api.createGraphWorktreeTask(workbenchId, worktreeId, { title, deviceId: newDevice, type: newType, intent: title, expectedResult: title })
      .then(() => {
        setNewTitle('')
        setNewType('feature')
        setNewDevice('')
        setCreateOpen(false)
        onCreateClosed?.()
        onChanged()
      })
      .catch(addError => showErrorToast(`Task could not be created: ${displayError(addError)}`))
      .finally(() => setAdding(false))
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
      <div className="flex justify-end">
        <button type="button" className="primary-button h-8" onClick={() => {
          setNewDevice(current => current || availableDevices[0]?.deviceId || '')
          setCreateOpen(true)
        }}>
          <Plus className="h-3.5 w-3.5" /> Add task
        </button>
      </div>

      {visibleTasks.length === 0 ? (
        <div className="grid place-items-center rounded-xl border border-dashed p-10 text-center" style={{ borderColor: 'var(--border)' }}>
          <ListTodo className="mb-3 h-6 w-6 text-chart-2" />
          <p className="text-[10px] text-muted-foreground">
            No tasks yet. Add the first modification task for this worktree above.
          </p>
        </div>
      ) : (
        taskStatusOrder.map(status => {
          const group = visibleTasks.filter(task => task.status === status)
          return (
            <section key={status} className="overflow-hidden rounded-xl border bg-card" style={{ borderColor: 'var(--border)' }}>
              <div className="flex items-center border-b px-4 py-2" style={{ borderColor: 'var(--border)' }}>
                <span className="text-[10px] font-semibold">{taskStatusLabel(status)}</span>
                <span className="ml-auto rounded bg-surface-muted px-1.5 py-0.5 font-mono text-[9px] text-muted-foreground">{group.length}</span>
              </div>
              {group.length === 0 ? (
                <div className="px-4 py-3 text-[9px] text-muted-foreground">No {taskStatusLabel(status).toLowerCase()} tasks.</div>
              ) : (
                <div className="divide-y" style={{ borderColor: 'var(--border)' }}>
                  {group.map(task => (
                    <div key={task.taskId} className="flex items-start gap-3 px-4 py-2.5">
                      {isLegacyTask(task) ? <TaskStatusControl
                        task={task}
                        onChange={next => mutate(() => api.updateWorktreeTask(workbenchId, worktreeId, task.taskId, { status: next }))}
                      /> : <span className="inline-flex shrink-0 rounded-full border px-2 py-0.5 text-[8px] uppercase tracking-[0.1em]">{taskStatusLabel(task.status as api.WorktreeTaskStatus)}</span>}
                      <div className="min-w-0 flex-1">
                        <div className={`text-[10px] font-medium ${task.status === 'done' ? 'text-muted-foreground line-through' : ''}`}>
                          {task.title}
                        </div>
                        <div className="mt-1 flex gap-1 text-[8px] text-muted-foreground">
                          <span className="rounded bg-surface-muted px-1.5 py-0.5">{task.scope === 'project' ? 'Project' : 'Worktree'} scope</span>
                          <span className="rounded bg-surface-muted px-1.5 py-0.5">{taskTypeLabel(task.type)}</span>
                        </div>
                        {(isLegacyTask(task) ? task.details : task.description) && (
                          <div className="mt-1 text-[9px] leading-relaxed text-muted-foreground [&_p]:my-1 [&_ul]:list-disc [&_ul]:pl-4">
                            <ReactMarkdown remarkPlugins={[remarkGfm]}>{isLegacyTask(task) ? task.details : task.description}</ReactMarkdown>
                          </div>
                        )}
                        {isLegacyTask(task) && task.elementRefs.length > 0 && (
                          <div className="mt-1.5 flex flex-wrap gap-1">
                            {task.elementRefs.map(elementRef => (
                              <span key={elementRef} className="rounded bg-surface-muted px-1.5 py-0.5 font-mono text-[8px] text-muted-foreground">
                                {elementRef}
                              </span>
                            ))}
                          </div>
                        )}
                      </div>
                      {onStartChat && <button type="button" className="secondary-button h-7 px-2 text-[9px]" aria-label={`Start chat for ${task.title}`} onClick={() => onStartChat(task)}>Start chat</button>}
                      {onOpenTaskDetail && !isLegacyTask(task) && <button type="button" className="secondary-button h-7 px-2 text-[9px]" aria-label={`Open task detail ${task.title}`} onClick={() => onOpenTaskDetail(task)}>Traceability</button>}
                      {isLegacyTask(task) && <><button className="icon-button" aria-label={`Edit task ${task.title}`} onClick={() => openEdit(task)}>
                        <Pencil className="h-3 w-3" />
                      </button>
                      <button
                        className="icon-button"
                        aria-label={`Delete task ${task.title}`}
                        onClick={() => {
                          if (window.confirm(`Delete task "${task.title}"?`)) {
                            mutate(() => api.deleteWorktreeTask(workbenchId, worktreeId, task.taskId))
                          }
                        }}
                      >
                        <Trash2 className="h-3 w-3" />
                      </button></>}
                    </div>
                  ))}
                </div>
              )}
            </section>
          )
        })
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
                <Input
                  aria-label="Task title"
                  value={draft.title}
                  onChange={event => setDraft({ ...draft, title: event.target.value })}
                />
              </label>
              <label className="field-label">
                <span>Details / modification plan (markdown)</span>
                <Textarea
                  aria-label="Task details"
                  className="min-h-[120px] resize-y py-1.5! font-mono text-[10px]!"
                  value={draft.details}
                  onChange={event => setDraft({ ...draft, details: event.target.value })}
                />
              </label>
              <div className="field-label">
                <span>PLC element references</span>
                <div className="mt-1 flex flex-wrap items-center gap-1">
                  {draft.elementRefs.map(elementRef => (
                    <span key={elementRef} className="inline-flex items-center gap-1 rounded bg-surface-muted px-1.5 py-0.5 font-mono text-[9px]">
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
                  <Input
                    aria-label="Add element reference"
                    className="h-6! w-44 text-[9px]!"
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
          <DialogFooter className="flex-row justify-end gap-2">
            <Button variant="primary" size="sm" onClick={() => setDraft(null)} disabled={savingEdit}>Cancel</Button>
            <Button variant="blue" size="sm" onClick={saveEdit} disabled={!draft?.title.trim() || savingEdit}>
              {savingEdit && <Loader2 className="h-3.5 w-3.5 animate-spin" />} Save task
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
      <Dialog open={createOpen} onOpenChange={setCreateOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Add task</DialogTitle>
            <DialogDescription>Create a focused task for this worktree.</DialogDescription>
          </DialogHeader>
          <form className="space-y-3" onSubmit={event => { event.preventDefault(); addTask() }}>
            <label className="field-label"><span>Title</span><Input autoFocus aria-label="New task title" value={newTitle} onChange={event => setNewTitle(event.target.value)} /></label>
            <label className="field-label"><span>Type</span><select aria-label="New task type" className="field-input" value={newType} onChange={event => setNewType(event.target.value as api.EngineeringTask['type'])}><option value="issue">Issue</option><option value="improvement">Improvement</option><option value="feature">Feature</option></select><span className="text-[9px] text-muted-foreground">Saved with the task’s modification plan.</span></label>
            <label className="field-label"><span>Device</span><select required aria-label="New task device" className="field-input" value={newDevice} onChange={event => setNewDevice(event.target.value)}><option value="">Select a device</option>{availableDevices.map(device => <option key={device.deviceId} value={device.deviceId}>{device.plcName || 'Unnamed PLC'}</option>)}</select><span className="text-[9px] text-muted-foreground">A task belongs to exactly one device. Add source objects from the task detail after creation.</span></label>
            <DialogFooter className="flex-row justify-end gap-2"><Button variant="primary" size="sm" type="button" onClick={() => { setCreateOpen(false); onCreateClosed?.() }} disabled={adding}>Cancel</Button><Button variant="blue" size="sm" type="submit" disabled={!newTitle.trim() || !newDevice || adding}>{adding && <Loader2 className="h-3.5 w-3.5 animate-spin" />} Create task</Button></DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
    </div>
  )
}
