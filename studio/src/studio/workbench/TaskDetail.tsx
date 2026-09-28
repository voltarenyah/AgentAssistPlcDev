import { useEffect, useState } from 'react'
import { AlertCircle, Loader2, MessageSquareText, RefreshCw, Save, X } from 'lucide-react'
import type { EngineeringTask, EngineeringTaskDetail, WorktreeTaskStatus } from '@/api/client'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'

export type TraceabilityItem = { id: string; edgeId: string; provenance: string; isPrimary: boolean }
export type TaskEditPatch = Pick<EngineeringTask, 'title' | 'type' | 'status' | 'priority' | 'intent' | 'expectedResult' | 'description'>

type TraceabilitySectionProps = {
  title: string
  items: TraceabilityItem[]
  emptyLabel: string
  onNavigate?: (id: string) => void
  onRemove?: (item: TraceabilityItem) => void
}

const provenanceLabel = (value: string) => {
  const normalized = value.toLowerCase()
  if (normalized === 'manual') return 'Manual link'
  if (normalized === 'evidence') return 'Evidence-derived'
  if (normalized === 'default') return 'Default link'
  return 'Unassigned'
}

export function TraceabilitySection({ title, items, emptyLabel, onNavigate, onRemove }: TraceabilitySectionProps) {
  return (
    <section className="overflow-hidden rounded-lg border bg-card" aria-label={title}>
      <header className="flex items-center border-b px-4 py-3">
        <h3 className="text-sm font-semibold">{title}</h3>
        <span className="ml-auto rounded bg-muted px-2 py-1 text-xs text-muted-foreground">{items.length}</span>
      </header>
      {items.length === 0 ? (
        <p className="px-4 py-4 text-sm text-muted-foreground">{emptyLabel}</p>
      ) : (
        <ul className="divide-y divide-border">
          {items.map(item => (
            <li key={item.id} className="flex min-w-0 items-center gap-3 px-4 py-3">
              {onNavigate ? (
                <button type="button" className="min-w-0 flex-1 truncate text-left font-mono text-sm underline-offset-2 hover:underline focus-visible:underline" aria-label={`Open ${title} ${item.id}`} onClick={() => onNavigate(item.id)}>{item.id}</button>
              ) : <span className="min-w-0 flex-1 truncate font-mono text-sm">{item.id}</span>}
              {item.isPrimary && <span className="rounded bg-muted px-2 py-1 text-xs">Primary</span>}
              <span className="shrink-0 text-xs text-muted-foreground">{provenanceLabel(item.provenance)}</span>
              {onRemove && item.provenance.toLowerCase() === 'manual' && <Button type="button" variant="outline" size="xs" aria-label={`Remove ${title} ${item.id}`} onClick={() => onRemove(item)}>Remove</Button>}
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

type TaskDraft = TaskEditPatch

const taskDraftFrom = (task: EngineeringTask): TaskDraft => ({
  title: task.title,
  type: task.type,
  status: task.status,
  priority: task.priority,
  intent: task.intent,
  expectedResult: task.expectedResult,
  description: task.description ?? '',
})

type Props = {
  detail: EngineeringTaskDetail | null
  deviceName?: string
  loading?: boolean
  error?: string | null
  saving?: boolean
  onRetry?: () => void
  onSave?: (patch: TaskEditPatch) => Promise<void>
  onNavigate?: (kind: string, id: string) => void
  onRemove?: (kind: string, item: TraceabilityItem) => void
}

export default function TaskDetail({ detail, deviceName, loading = false, error = null, saving = false, onRetry, onSave, onNavigate, onRemove }: Props) {
  const [draft, setDraft] = useState<TaskDraft | null>(detail ? taskDraftFrom(detail.task) : null)
  const [saveError, setSaveError] = useState<string | null>(null)

  useEffect(() => {
    setDraft(detail ? taskDraftFrom(detail.task) : null)
    setSaveError(null)
  }, [detail])

  if (loading) return <div className="flex items-center justify-center gap-2 p-10 text-sm text-muted-foreground" role="status"><Loader2 className="h-4 w-4 animate-spin" /> Loading task details...</div>
  if (error) return <div className="flex items-center gap-3 rounded-lg border p-5 text-sm text-muted-foreground" role="alert"><AlertCircle className="h-4 w-4 shrink-0 text-red-500" /><span className="min-w-0 flex-1">Task details could not be loaded: {error}</span>{onRetry && <Button type="button" variant="outline" size="sm" onClick={onRetry}><RefreshCw className="h-4 w-4" /> Retry</Button>}</div>
  if (!detail || !draft) return null

  const sections: Array<[string, string, EngineeringTaskDetail['commits']]> = [
    ['Commits', 'commit', detail.commits],
    ['Source objects', 'sourceObject', detail.sourceObjects],
    ['SVN revisions', 'svnRevision', detail.svnRevisions],
  ]
  const dirty = Object.keys(draft).some(key => draft[key as keyof TaskDraft] !== taskDraftFrom(detail.task)[key as keyof TaskDraft])
  const fieldClass = 'mt-1.5 w-full'

  const save = async () => {
    if (!onSave || !dirty || saving) return
    const title = draft.title.trim()
    const intent = draft.intent.trim()
    const expectedResult = draft.expectedResult.trim()
    if (!title || !intent || !expectedResult) {
      setSaveError('Title, intent, and expected result are required.')
      return
    }
    setSaveError(null)
    try {
      await onSave({ ...draft, title, intent, expectedResult, description: draft.description?.trim() ?? '' })
    } catch (reason) {
      setSaveError(reason instanceof Error ? reason.message : 'Task changes could not be saved.')
    }
  }

  const sessions = detail.sessions
  const otherItems = sections.filter(([, , items]) => items.length > 0)
  const updateDraft = <K extends keyof TaskDraft>(key: K, value: TaskDraft[K]) => setDraft(previous => previous ? { ...previous, [key]: value } : previous)

  return <article className="mx-auto w-full max-w-6xl space-y-5" aria-label={`Task detail: ${detail.task.title}`}>
    <header className="flex flex-wrap items-end justify-between gap-3">
      <div>
        <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">Task detail</p>
        <h1 className="mt-1 text-xl font-semibold tracking-tight">{detail.task.title}</h1>
      </div>
      <span className="rounded-full bg-muted px-3 py-1 text-sm text-muted-foreground">{detail.task.scope === 'project' ? 'Project task' : 'Worktree task'}</span>
    </header>

    <section className="rounded-xl border bg-card p-5" aria-label="Task fields">
      <div className="mb-5 flex items-center justify-between border-b pb-3">
        <div><h2 className="text-sm font-semibold">Task fields</h2><p className="mt-1 text-xs text-muted-foreground">Edit the task details and save your changes.</p></div>
        {onSave && <div className="flex gap-2">
          <Button type="button" variant="outline" size="sm" disabled={!dirty || saving} onClick={() => { setDraft(taskDraftFrom(detail.task)); setSaveError(null) }}><X className="h-4 w-4" /> Discard</Button>
          <Button type="button" size="sm" disabled={!dirty || saving} onClick={() => void save()}>{saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}{saving ? 'Saving…' : 'Save changes'}</Button>
        </div>}
      </div>
      <div className="grid gap-x-5 gap-y-4 md:grid-cols-2">
        <div className="md:col-span-2"><Label htmlFor="task-title">Title</Label><Input id="task-title" className={fieldClass} value={draft.title} onChange={event => updateDraft('title', event.target.value)} /></div>
        <div><Label htmlFor="task-type">Type</Label><Select value={draft.type} onValueChange={value => updateDraft('type', value as EngineeringTask['type'])}><SelectTrigger id="task-type" className={fieldClass}><SelectValue /></SelectTrigger><SelectContent><SelectItem value="issue">Issue</SelectItem><SelectItem value="improvement">Improvement</SelectItem><SelectItem value="feature">Feature</SelectItem></SelectContent></Select></div>
        <div><Label htmlFor="task-status">Status</Label><Select value={draft.status} onValueChange={value => updateDraft('status', value as WorktreeTaskStatus)}><SelectTrigger id="task-status" className={fieldClass}><SelectValue /></SelectTrigger><SelectContent><SelectItem value="todo">Todo</SelectItem><SelectItem value="inProgress">In progress</SelectItem><SelectItem value="done">Done</SelectItem></SelectContent></Select></div>
        <div><Label htmlFor="task-priority">Priority</Label><Input id="task-priority" type="number" min="0" step="1" className={fieldClass} value={draft.priority} onChange={event => updateDraft('priority', Math.max(0, Number(event.target.value) || 0))} /></div>
        <div><Label htmlFor="task-device">Device / PLC</Label><Input id="task-device" className={fieldClass} value={detail.task.deviceId ? deviceName || 'Unknown device' : 'Not device-bound'} readOnly aria-describedby="task-device-note" /><p id="task-device-note" className="mt-1.5 text-xs text-muted-foreground">Device binding is fixed when the task is created.</p></div>
        <div className="md:col-span-2"><Label htmlFor="task-intent">Intent</Label><textarea id="task-intent" className={`${fieldClass} min-h-20 rounded-md border border-input bg-transparent px-3 py-2 text-sm outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50`} value={draft.intent} onChange={event => updateDraft('intent', event.target.value)} /></div>
        <div className="md:col-span-2"><Label htmlFor="task-expected-result">Expected result</Label><textarea id="task-expected-result" className={`${fieldClass} min-h-20 rounded-md border border-input bg-transparent px-3 py-2 text-sm outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50`} value={draft.expectedResult} onChange={event => updateDraft('expectedResult', event.target.value)} /></div>
        <div className="md:col-span-2"><Label htmlFor="task-description">Description</Label><textarea id="task-description" className={`${fieldClass} min-h-24 rounded-md border border-input bg-transparent px-3 py-2 text-sm outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50`} value={draft.description ?? ''} onChange={event => updateDraft('description', event.target.value)} /></div>
      </div>
      {saveError && <p className="mt-4 text-sm text-destructive" role="alert">{saveError}</p>}
      <dl className="mt-5 grid gap-3 border-t pt-4 text-xs sm:grid-cols-2 lg:grid-cols-4">
        <div><dt className="text-muted-foreground">Task ID</dt><dd className="mt-1 break-all font-mono">{detail.task.taskId}</dd></div>
        <div><dt className="text-muted-foreground">Worktree</dt><dd className="mt-1 break-all font-mono">{detail.task.worktreeId || 'Project scope'}</dd></div>
        <div><dt className="text-muted-foreground">Created</dt><dd className="mt-1">{detail.task.createdUtc ? new Date(detail.task.createdUtc).toLocaleString() : '—'}</dd></div>
        <div><dt className="text-muted-foreground">Updated</dt><dd className="mt-1">{detail.task.updatedUtc ? new Date(detail.task.updatedUtc).toLocaleString() : '—'}</dd></div>
      </dl>
    </section>

    <section className="overflow-hidden rounded-xl border bg-card" aria-label="Associated sessions">
      <header className="flex items-center gap-2 border-b px-4 py-3"><MessageSquareText className="h-4 w-4 text-muted-foreground" /><h2 className="text-sm font-semibold">Sessions</h2><span className="ml-auto rounded bg-muted px-2 py-1 text-xs text-muted-foreground">{sessions.length}</span></header>
      {sessions.length === 0 ? <p className="px-4 py-4 text-sm text-muted-foreground">No conversations linked to this task yet.</p> : <ul className="divide-y divide-border">{sessions.map(session => <li key={session.id} className="flex min-w-0 flex-wrap items-center gap-x-4 gap-y-2 px-4 py-4">
        <div className="min-w-0 flex-1"><p className="truncate text-sm font-medium">{session.title?.trim() || session.firstUserMessage?.trim() || 'Untitled conversation'}</p><p className="mt-1 truncate font-mono text-xs text-muted-foreground">{session.id}</p>{session.firstUserMessage && session.title?.trim() && <p className="mt-1 truncate text-xs text-muted-foreground">{session.firstUserMessage}</p>}</div>
        <span className="text-xs text-muted-foreground">{session.turnCount ?? 0} turns · {provenanceLabel(session.provenance)}{session.isPrimary ? ' · Primary' : ''}</span>
        {onNavigate && <Button type="button" variant="outline" size="sm" aria-label={`Open session ${session.title?.trim() || session.id}`} onClick={() => onNavigate('session', session.id)}>Open conversation</Button>}
        {onRemove && session.provenance.toLowerCase() === 'manual' && <Button type="button" variant="outline" size="xs" aria-label={`Remove Sessions ${session.id}`} onClick={() => onRemove('session', session)}>Remove</Button>}
      </li>)}</ul>}
    </section>

    {otherItems.length > 0 ? <div className="grid gap-4 md:grid-cols-2">{otherItems.map(([title, kind, items]) => <TraceabilitySection key={kind} title={title} items={items} emptyLabel={`No linked ${title.toLowerCase()} yet.`} onNavigate={onNavigate ? id => onNavigate(kind, id) : undefined} onRemove={onRemove ? item => onRemove(kind, item) : undefined} />)}</div> : <p className="text-sm text-muted-foreground">No commits, source objects, or SVN revisions linked yet.</p>}
  </article>
}
