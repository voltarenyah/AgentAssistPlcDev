import { useEffect, useState } from 'react'
import { AlertCircle, Loader2, MessageSquareText, Plus, RefreshCw, Save, X } from 'lucide-react'
import type { EngineeringTask, EngineeringTaskDetail, WorktreeTaskStatus } from '@/api/client'
import { taskTargetKind } from '@/api/client'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import TaskCommitsSection from './TaskCommitsSection'
import TaskSourceObjectsSection from './TaskSourceObjectsSection'

export type TraceabilityItem = { id: string; edgeId: string; provenance: string; isPrimary: boolean }
export type TaskEditPatch = Pick<EngineeringTask, 'title' | 'type' | 'status' | 'priority' | 'intent' | 'expectedResult' | 'description'>

/** One traceability row in the Related records group, carrying the kind it navigates and removes as. */
type RelatedRecord = TraceabilityItem & { kind: string; kindLabel: string }

/**
 * The provenance a relation carries, said the way the graph stores it. `auto` is the relation a
 * conversation established by creating the task itself, so it names the conversation rather than
 * reading as an unclassified link (ADR-0014).
 */
const provenanceLabel = (value: string) => {
  const normalized = value.toLowerCase()
  if (normalized === 'manual') return 'Manual link'
  if (normalized === 'auto') return 'Created by this conversation'
  if (normalized === 'evidence') return 'Evidence-derived'
  if (normalized === 'default') return 'Default link'
  return 'Unassigned'
}

/**
 * Whether the relation can be cleared from the page. A manual link and one the conversation created
 * itself are both removable — an automatic relation that could not be cleared would leave the user
 * with a link the conversation made and no way back (ADR-0014, AC-019) — while a default link is
 * cleared from the conversation's own picker instead.
 */
const removableProvenance = (value: string) => ['manual', 'auto'].includes(value.toLowerCase())

/**
 * The remaining traceability edges — the graph's source-object links and the task's SVN revisions.
 * They are read-only history: only a manual link can be removed. The editable, stage-backed list is
 * the separate Source objects section, so no two sections claim the same meaning.
 */
function RelatedRecordsSection({ items, onNavigate, onRemove }: {
  items: RelatedRecord[]
  onNavigate?: (kind: string, id: string) => void
  onRemove?: (kind: string, item: TraceabilityItem) => void
}) {
  return (
    <section className="overflow-hidden rounded-lg border bg-card" aria-label="Related records">
      <header className="flex items-center border-b px-4 py-3">
        <h3 className="text-sm font-semibold">Related records</h3>
        <span className="ml-auto rounded bg-muted px-2 py-1 text-xs text-muted-foreground">{items.length}</span>
      </header>
      {items.length === 0 ? (
        <p className="px-4 py-4 text-sm text-muted-foreground">No related records yet.</p>
      ) : (
        <ul className="divide-y divide-border">
          {items.map(item => (
            <li key={`${item.kind}:${item.id}`} className="flex min-w-0 items-center gap-3 px-4 py-3">
              <span className="shrink-0 rounded bg-muted px-2 py-1 text-xs text-muted-foreground">{item.kindLabel}</span>
              {onNavigate ? (
                <button type="button" className="min-w-0 flex-1 truncate text-left font-mono text-sm underline-offset-2 hover:underline focus-visible:underline" aria-label={`Open ${item.kindLabel} ${item.id}`} onClick={() => onNavigate(item.kind, item.id)}>{item.id}</button>
              ) : <span className="min-w-0 flex-1 truncate font-mono text-sm">{item.id}</span>}
              {item.isPrimary && <span className="rounded bg-muted px-2 py-1 text-xs">Primary</span>}
              <span className="shrink-0 text-xs text-muted-foreground">{provenanceLabel(item.provenance)}</span>
              {onRemove && item.provenance.toLowerCase() === 'manual' && <Button type="button" variant="outline" size="xs" aria-label={`Remove ${item.kindLabel} ${item.id}`} onClick={() => onRemove(item.kind, { id: item.id, edgeId: item.edgeId, provenance: item.provenance, isPrimary: item.isPrimary })}>Remove</Button>}
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
  /**
   * Starts a conversation bound to this task and opens it. Offered only for a task that can own one —
   * a device-bound worktree task — because a conversation resolves through a device.
   */
  onStartSession?: (task: EngineeringTask) => void
  /** Raised after a stage change, so the page can reload the task's traceability edges. */
  onStagesChanged?: () => void
  /** Bumped when another surface changed the stages (an approved agent stage call), so the Source
   * objects section re-reads its list. */
  stagesRefreshToken?: number
}

export default function TaskDetail({ detail, deviceName, loading = false, error = null, saving = false, onRetry, onSave, onNavigate, onRemove, onStartSession, onStagesChanged, stagesRefreshToken = 0 }: Props) {
  const [draft, setDraft] = useState<TaskDraft | null>(detail ? taskDraftFrom(detail.task) : null)
  const [saveError, setSaveError] = useState<string | null>(null)
  // A hardware task has no device, so "Not device-bound" would state the opposite of the truth.
  const hardwareTask = detail ? taskTargetKind(detail.task) === 'hardware' : false

  useEffect(() => {
    setDraft(detail ? taskDraftFrom(detail.task) : null)
    setSaveError(null)
  }, [detail])

  if (loading) return <div className="flex items-center justify-center gap-2 p-10 text-sm text-muted-foreground" role="status"><Loader2 className="h-4 w-4 animate-spin" /> Loading task details...</div>
  if (error) return <div className="flex items-center gap-3 rounded-lg border p-5 text-sm text-muted-foreground" role="alert"><AlertCircle className="h-4 w-4 shrink-0 text-red-500" /><span className="min-w-0 flex-1">Task details could not be loaded: {error}</span>{onRetry && <Button type="button" variant="outline" size="sm" onClick={onRetry}><RefreshCw className="h-4 w-4" /> Retry</Button>}</div>
  if (!detail || !draft) return null

  // Only a device-bound worktree task can stage source objects (ADR-0003); everything else states
  // why instead of offering controls that cannot work.
  const stageable = detail.task.scope !== 'project' && !hardwareTask
    && Boolean(detail.task.worktreeId) && Boolean(detail.task.deviceId)
  const stagingExplanation = detail.task.scope === 'project'
    ? 'Source objects are staged by a device-bound worktree task, and this is a project-scope task with no device to stage them from.'
    : 'Source objects are staged by a device-bound worktree task, and this hardware task binds no PLC device.'
  const relatedRecords: RelatedRecord[] = [
    ...detail.sourceObjects.map(item => ({ ...item, kind: 'sourceObject', kindLabel: 'Source object' })),
    ...detail.svnRevisions.map(item => ({ ...item, kind: 'svnRevision', kindLabel: 'SVN revision' })),
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
  // A conversation resolves through a device, so only a device-bound worktree task can own one. That is
  // the condition staging needs too, but it is asked here for its own reason: a project-scope or
  // hardware task offers no creation control rather than one that cannot work.
  const conversationCapable = detail.task.scope !== 'project' && !hardwareTask
    && Boolean(detail.task.worktreeId) && Boolean(detail.task.deviceId)
  const startSessionAction = onStartSession && conversationCapable
    ? <Button type="button" variant="outline" size="xs" aria-label={`New chat for ${detail.task.title}`} onClick={() => onStartSession(detail.task)}><Plus className="h-3 w-3" /> New chat</Button>
    : null
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
        <div><Label htmlFor="task-device">{hardwareTask ? 'Target' : 'Device / PLC'}</Label><Input id="task-device" className={fieldClass} value={hardwareTask ? 'Hardware configuration' : detail.task.deviceId ? deviceName || 'Unknown device' : 'Not device-bound'} readOnly aria-describedby="task-device-note" /><p id="task-device-note" className="mt-1.5 text-xs text-muted-foreground">{hardwareTask ? 'A hardware task covers this worktree’s hardware configuration and binds no PLC device; stage source objects from a device task instead.' : 'Device binding is fixed when the task is created.'}</p></div>
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

    {stageable ? (
      <TaskSourceObjectsSection
        workbenchId={detail.task.workbenchId}
        worktreeId={detail.task.worktreeId!}
        taskId={detail.task.taskId}
        deviceId={detail.task.deviceId!}
        deviceName={deviceName}
        refreshToken={stagesRefreshToken}
        onChanged={onStagesChanged}
      />
    ) : <p className="rounded-lg border bg-card px-4 py-4 text-sm text-muted-foreground">{stagingExplanation}</p>}

    <section className="overflow-hidden rounded-xl border bg-card" aria-label="Associated sessions">
      <header className="flex items-center gap-2 border-b px-4 py-3"><MessageSquareText className="h-4 w-4 text-muted-foreground" /><h2 className="text-sm font-semibold">Sessions</h2><span className="rounded bg-muted px-2 py-1 text-xs text-muted-foreground">{sessions.length}</span>{startSessionAction && <span className="ml-auto">{startSessionAction}</span>}</header>
      {sessions.length === 0 ? <p className="px-4 py-4 text-sm text-muted-foreground">No conversations linked to this task yet.{startSessionAction ? ' Start one with New chat.' : ''}</p> : <ul className="divide-y divide-border">{sessions.map(session => <li key={session.id} className="flex min-w-0 flex-wrap items-center gap-x-4 gap-y-2 px-4 py-4">
        <div className="min-w-0 flex-1"><p className="truncate text-sm font-medium">{session.title?.trim() || session.firstUserMessage?.trim() || 'Untitled conversation'}</p><p className="mt-1 truncate font-mono text-xs text-muted-foreground">{session.id}</p>{session.firstUserMessage && session.title?.trim() && <p className="mt-1 truncate text-xs text-muted-foreground">{session.firstUserMessage}</p>}</div>
        <span className="text-xs text-muted-foreground">{session.turnCount ?? 0} turns · {provenanceLabel(session.provenance)}{session.isPrimary ? ' · Primary' : ''}</span>
        {onNavigate && <Button type="button" variant="outline" size="sm" aria-label={`Open session ${session.title?.trim() || session.id}`} onClick={() => onNavigate('session', session.id)}>Open conversation</Button>}
        {onRemove && removableProvenance(session.provenance) && <Button type="button" variant="outline" size="xs" aria-label={`Remove Sessions ${session.id}`} onClick={() => onRemove('session', session)}>Remove</Button>}
      </li>)}</ul>}
    </section>

    {detail.commits.length > 0 || relatedRecords.length > 0 ? <div className="grid gap-4 md:grid-cols-2">
      {detail.commits.length > 0 && <TaskCommitsSection
        workbenchId={detail.task.workbenchId}
        worktreeId={detail.task.worktreeId}
        commits={detail.commits}
        onNavigate={onNavigate}
        onRemove={onRemove}
      />}
      {relatedRecords.length > 0 && <RelatedRecordsSection items={relatedRecords} onNavigate={onNavigate} onRemove={onRemove} />}
    </div> : <p className="text-sm text-muted-foreground">No commits or related records linked yet.</p>}
  </article>
}
