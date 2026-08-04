import { useState } from 'react'
import { Check, ChevronDown, ListTodo, Loader2, Pencil, Plus, Trash2, X } from 'lucide-react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import * as api from '@/api/client'
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

type Props = {
  workbenchId: string
  worktreeId: string
  tasks: api.WorktreeTask[]
  loading: boolean
  error: string | null
  onChanged: () => void
}

const displayError = (error: unknown) => {
  if (error instanceof api.WorkbenchApiError) return `${error.code}: ${error.message}`
  return error instanceof Error ? error.message : 'Unexpected operation failure'
}

const taskStatusLabel = (status: api.WorktreeTaskStatus) =>
  status === 'inProgress' ? 'In Progress' : status === 'done' ? 'Done' : 'Todo'

const taskStatusOrder: api.WorktreeTaskStatus[] = ['todo', 'inProgress', 'done']

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

export default function WorktreeTasksPanel({ workbenchId, worktreeId, tasks, loading, error, onChanged }: Props) {
  const [newTitle, setNewTitle] = useState('')
  const [adding, setAdding] = useState(false)
  const [draft, setDraft] = useState<EditDraft | null>(null)
  const [newRef, setNewRef] = useState('')
  const [savingEdit, setSavingEdit] = useState(false)

  const mutate = (action: () => Promise<unknown>) => {
    void action()
      .then(onChanged)
      .catch(mutationError => showErrorToast(`Task could not be updated: ${displayError(mutationError)}`))
  }

  const addTask = () => {
    const title = newTitle.trim()
    if (!title || adding) return
    setAdding(true)
    void api.createWorktreeTask(workbenchId, worktreeId, { title })
      .then(() => {
        setNewTitle('')
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
      <div className="flex gap-2">
        <input
          aria-label="New task title"
          className="field-input h-8 flex-1 text-[10px]"
          placeholder="Add a task — which PLC element needs modification?"
          value={newTitle}
          onChange={event => setNewTitle(event.target.value)}
          onKeyDown={event => {
            if (event.key === 'Enter') {
              event.preventDefault()
              addTask()
            }
          }}
        />
        <button className="primary-button h-8" disabled={!newTitle.trim() || adding} onClick={addTask}>
          {adding ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Plus className="h-3.5 w-3.5" />} Add task
        </button>
      </div>

      {tasks.length === 0 ? (
        <div className="grid place-items-center rounded-xl border border-dashed p-10 text-center" style={{ borderColor: 'var(--border)' }}>
          <ListTodo className="mb-3 h-6 w-6 text-chart-2" />
          <p className="text-[10px] text-muted-foreground">
            No tasks yet. Add the first modification task for this worktree above.
          </p>
        </div>
      ) : (
        taskStatusOrder.map(status => {
          const group = tasks.filter(task => task.status === status)
          return (
            <section key={status} className="overflow-hidden rounded-xl border bg-card" style={{ borderColor: 'var(--border)' }}>
              <div className="flex items-center border-b px-4 py-2" style={{ borderColor: 'var(--border)' }}>
                <span className="text-[10px] font-semibold">{taskStatusLabel(status)}</span>
                <span className="ml-auto rounded bg-muted px-1.5 py-0.5 font-mono text-[9px] text-muted-foreground">{group.length}</span>
              </div>
              {group.length === 0 ? (
                <div className="px-4 py-3 text-[9px] text-muted-foreground">No {taskStatusLabel(status).toLowerCase()} tasks.</div>
              ) : (
                <div className="divide-y" style={{ borderColor: 'var(--border)' }}>
                  {group.map(task => (
                    <div key={task.taskId} className="flex items-start gap-3 px-4 py-2.5">
                      <TaskStatusControl
                        task={task}
                        onChange={next => mutate(() => api.updateWorktreeTask(workbenchId, worktreeId, task.taskId, { status: next }))}
                      />
                      <div className="min-w-0 flex-1">
                        <div className={`text-[10px] font-medium ${task.status === 'done' ? 'text-muted-foreground line-through' : ''}`}>
                          {task.title}
                        </div>
                        {task.details && (
                          <div className="mt-1 text-[9px] leading-relaxed text-muted-foreground [&_p]:my-1 [&_ul]:list-disc [&_ul]:pl-4">
                            <ReactMarkdown remarkPlugins={[remarkGfm]}>{task.details}</ReactMarkdown>
                          </div>
                        )}
                        {task.elementRefs.length > 0 && (
                          <div className="mt-1.5 flex flex-wrap gap-1">
                            {task.elementRefs.map(elementRef => (
                              <span key={elementRef} className="rounded bg-muted px-1.5 py-0.5 font-mono text-[8px] text-muted-foreground">
                                {elementRef}
                              </span>
                            ))}
                          </div>
                        )}
                      </div>
                      <button className="icon-button" aria-label={`Edit task ${task.title}`} onClick={() => openEdit(task)}>
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
                      </button>
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
    </div>
  )
}
