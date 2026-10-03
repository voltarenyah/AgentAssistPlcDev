import { useCallback, useEffect, useMemo, useState } from 'react'
import { AlertCircle, Loader2, Plus } from 'lucide-react'
import * as api from '@/api/client'
import type { SourceObjectInfo, TaskSourceStage, WorktreeSourceStage } from '@/api/client'
import { Button } from '@/components/ui/button'
import { CommandDialog, CommandEmpty, CommandInput, CommandItem, CommandList } from '@/components/ui/command'
import { showErrorToast } from '@/components/ui/toast'
import {
  countSourceObjectsByType,
  filterSourceObjects,
  limitSourceObjects,
  SOURCE_TYPE_FILTERS,
  type SourceTypeFilter,
} from '@/studio/plcSourceState'

/**
 * The task page's editable Source objects section: the task's active stages (its task-scoped TIA
 * compare basis), with a searchable picker over the bound device's source objects.
 *
 * A stage's fingerprint baseline is bound to the object's committed Git content, so an object the
 * committed manifest does not list yet has no baseline; the row states that instead of implying the
 * object is comparable. One active owner exists per source object per worktree, so an object owned
 * by another task shows its owner and needs an explicit take-over.
 */
type Props = {
  workbenchId: string
  worktreeId: string
  taskId: string
  deviceId: string
  deviceName?: string
  /**
   * Bumped when another surface changed the task's stages — the agent's approved
   * `stage_task_source_object` call — so the section re-reads the stage list instead of keeping the
   * pre-approval rows it already has.
   */
  refreshToken?: number
  /** Notifies the page that the stage list (and therefore its traceability edges) changed. */
  onChanged?: () => void
}

const errorMessage = (error: unknown) => (error instanceof Error ? error.message : String(error))

const stageIdentity = (deviceId: string, sourceId: string) => `${deviceId}:${sourceId}`

export default function TaskSourceObjectsSection({ workbenchId, worktreeId, taskId, deviceId, deviceName, refreshToken = 0, onChanged }: Props) {
  const [stages, setStages] = useState<TaskSourceStage[]>([])
  const [owners, setOwners] = useState<WorktreeSourceStage[]>([])
  const [candidates, setCandidates] = useState<SourceObjectInfo[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [pending, setPending] = useState<string | null>(null)
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const [typeFilter, setTypeFilter] = useState<SourceTypeFilter>('all')

  const load = useCallback(async () => {
    setLoading(true)
    try {
      const [taskStages, worktreeStages, sourceObjects] = await Promise.all([
        api.listTaskSourceStages(workbenchId, worktreeId, taskId),
        api.listWorktreeSourceStages(workbenchId, worktreeId),
        // The source objects alone: the full device snapshot also crawls every block on disk, which
        // costs seconds on a large export and none of it is shown here.
        api.listDeviceSourceObjects(workbenchId, worktreeId, deviceId),
      ])
      setStages(taskStages)
      setOwners(worktreeStages)
      setCandidates(sourceObjects)
      setError(null)
    } catch (caught) {
      setError(errorMessage(caught))
    } finally {
      setLoading(false)
    }
  }, [workbenchId, worktreeId, taskId, deviceId, refreshToken])

  useEffect(() => { void load() }, [load])

  const counts = useMemo(() => countSourceObjectsByType(candidates), [candidates])
  const matching = useMemo(() => filterSourceObjects(candidates, typeFilter, query), [candidates, typeFilter, query])
  const limited = useMemo(() => limitSourceObjects(matching), [matching])
  const stagedIds = useMemo(() => new Set(stages.map(stage => stage.sourceObjectId)), [stages])
  const infoById = useMemo(
    () => new Map(candidates.map(item => [stageIdentity(deviceId, item.id), item])),
    [candidates, deviceId],
  )
  const ownerById = useMemo(() => {
    const byId = new Map<string, WorktreeSourceStage>()
    for (const owner of owners) {
      if (owner.taskId !== taskId) byId.set(owner.sourceObjectId, owner)
    }
    return byId
  }, [owners, taskId])

  const changed = async () => {
    await load()
    onChanged?.()
  }

  /** Shows a just-staged object in the list behind the dialog without waiting for the reload. */
  const rememberStage = (created: TaskSourceStage) =>
    setStages(previous => previous.some(stage => stage.sourceObjectId === created.sourceObjectId)
      ? previous
      : [...previous, created].sort((left, right) => left.sourceObjectId.localeCompare(right.sourceObjectId)))

  const stage = async (sourceObjectId: string) => {
    setPending(sourceObjectId)
    try {
      const created = await api.stageTaskSourceObject(workbenchId, worktreeId, taskId, sourceObjectId)
      // The response is the server's confirmation of what changed, so the list shows it at once.
      // Re-reading the whole list here can answer with a pre-click snapshot and leave the list looking
      // unchanged; the section re-reads on mount, when its context changes, and after a release.
      rememberStage(created)
      setOpen(false)
      setQuery('')
      onChanged?.()
    } catch (caught) {
      showErrorToast(errorMessage(caught))
    } finally {
      setPending(null)
    }
  }

  /** Take-over is explicit: the current owner releases the object first, then this task stages it. */
  const takeOver = async (owner: WorktreeSourceStage, sourceObjectId: string) => {
    setPending(sourceObjectId)
    try {
      await api.releaseTaskSourceObject(workbenchId, worktreeId, owner.taskId, sourceObjectId)
      const created = await api.stageTaskSourceObject(workbenchId, worktreeId, taskId, sourceObjectId)
      rememberStage(created)
      setOpen(false)
      setQuery('')
      onChanged?.()
    } catch (caught) {
      showErrorToast(errorMessage(caught))
    } finally {
      setPending(null)
    }
  }

  const remove = async (sourceObjectId: string) => {
    setPending(sourceObjectId)
    try {
      await api.releaseTaskSourceObject(workbenchId, worktreeId, taskId, sourceObjectId)
      await changed()
    } catch (caught) {
      showErrorToast(errorMessage(caught))
    } finally {
      setPending(null)
    }
  }

  const addAction = (
    <Button
      type="button"
      variant="outline"
      size="xs"
      aria-label="Add source object"
      disabled={loading || pending !== null || candidates.length === 0}
      onClick={() => setOpen(true)}
    >
      <Plus className="h-3 w-3" /> Add source object
    </Button>
  )

  const rowFor = (sourceObjectId: string, baselineEvidenceJson: string | null) => {
    const info = infoById.get(sourceObjectId)
    const category = info?.category ?? '—'
    const name = info?.name ?? sourceObjectId
    return (
      <li key={sourceObjectId} className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1 px-4 py-3">
        <span className="shrink-0 rounded bg-muted px-2 py-1 text-xs text-muted-foreground">{category}</span>
        <span className="min-w-0 flex-1 truncate text-sm">{name}</span>
        <span className="shrink-0 text-xs text-muted-foreground">{deviceName || deviceId}</span>
        {baselineEvidenceJson === null && (
          <span className="w-full text-xs text-muted-foreground" data-testid="stage-baseline-missing">
            {info?.evidenceKind === 'instance-db'
              // Instance DBs are excluded from managed-source evidence by design, so no commit can
              // ever give one a baseline; telling the reader to commit it would be false advice.
              ? 'Instance DBs are excluded from fingerprint comparison, so this object has no baseline and committing it will not create one.'
              : 'No committed Git content yet, so this object has no fingerprint baseline. Commit it once; comparison reports it until then.'}
          </span>
        )}
        <Button
          type="button"
          variant="outline"
          size="xs"
          aria-label={`Remove source object ${name}`}
          disabled={pending !== null}
          onClick={() => void remove(sourceObjectId)}
        >
          Remove
        </Button>
      </li>
    )
  }

  return (
    <section className="overflow-hidden rounded-lg border bg-card" aria-label="Source objects">
      <header className="flex items-center gap-2 border-b px-4 py-3">
        <h3 className="text-sm font-semibold">Source objects</h3>
        <span className="rounded bg-muted px-2 py-1 text-xs text-muted-foreground">{stages.length}</span>
        <span className="ml-auto">{stages.length > 0 ? addAction : null}</span>
      </header>

      {loading ? (
        <p className="flex items-center gap-2 px-4 py-4 text-sm text-muted-foreground" role="status">
          <Loader2 className="h-4 w-4 animate-spin" /> Loading staged source objects...
        </p>
      ) : error ? (
        <div className="flex items-center gap-3 px-4 py-4 text-sm text-muted-foreground" role="alert">
          <AlertCircle className="h-4 w-4 shrink-0 text-red-500" />
          <span className="min-w-0 flex-1">Staged source objects could not be loaded: {error}</span>
          <Button type="button" variant="outline" size="sm" onClick={() => void load()}>Retry</Button>
        </div>
      ) : stages.length === 0 ? (
        <div className="flex flex-wrap items-center gap-3 px-4 py-4">
          <p className="min-w-0 flex-1 text-sm text-muted-foreground">No source objects staged yet.</p>
          {addAction}
        </div>
      ) : (
        <ul className="divide-y divide-border">
          {stages.map(stage => rowFor(stage.sourceObjectId, stage.baselineEvidenceJson))}
        </ul>
      )}

      <CommandDialog
        open={open}
        onOpenChange={setOpen}
        title="Add source object"
        description="Search this task's device source objects and stage one"
        shouldFilter={false}
      >
        <CommandInput
          value={query}
          onValueChange={setQuery}
          onInput={event => setQuery(event.currentTarget.value)}
          placeholder="Search source objects…"
          aria-label="Search source objects"
        />
        <div className="flex flex-wrap gap-1 border-b px-3 py-2">
          {SOURCE_TYPE_FILTERS.map(filter => (
            <Button
              key={filter.id}
              type="button"
              size="xs"
              variant={typeFilter === filter.id ? 'secondary' : 'ghost'}
              aria-pressed={typeFilter === filter.id}
              aria-label={`Filter by ${filter.label}`}
              onClick={() => setTypeFilter(filter.id)}
            >
              {filter.label} <span className="text-muted-foreground">{counts[filter.id]}</span>
            </Button>
          ))}
        </div>
        <CommandList>
          <CommandEmpty>No source objects match.</CommandEmpty>
          {limited.items.map(item => {
            const sourceObjectId = stageIdentity(deviceId, item.id)
            const owner = ownerById.get(sourceObjectId)
            const staged = stagedIds.has(sourceObjectId)
            return (
              <CommandItem
                key={sourceObjectId}
                value={sourceObjectId}
                aria-label={`Stage ${item.category} ${item.name}`}
                aria-busy={pending === sourceObjectId}
                className={staged ? 'opacity-50' : undefined}
                // An object owned by another task never stages implicitly: the take-over control
                // below is the only path, and it releases the current owner first. While one request
                // is in flight the rows stop accepting a second one.
                onSelect={() => { if (pending === null && !staged && !owner) void stage(sourceObjectId) }}
              >
                <span className="shrink-0 rounded bg-muted px-2 py-0.5 text-xs text-muted-foreground">{item.category}</span>
                <span className="min-w-0 flex-1 truncate">{item.name}</span>
                <span className="shrink-0 text-xs text-muted-foreground">
                  {staged ? 'Staged by this task' : owner ? `Owned by ${owner.taskTitle}` : item.relativePath}
                </span>
                {pending === sourceObjectId && (
                  <span
                    className="flex shrink-0 items-center gap-1 text-xs text-muted-foreground"
                    role="status"
                    data-testid={`stage-pending-${sourceObjectId}`}
                  >
                    <Loader2 className="h-3 w-3 animate-spin" />
                    {owner ? 'Taking over…' : 'Staging…'}
                  </span>
                )}
                {owner && (
                  <Button
                    type="button"
                    variant="outline"
                    size="xs"
                    aria-label={`Take over ${item.category} ${item.name} from ${owner.taskTitle}`}
                    disabled={pending !== null}
                    onClick={event => { event.stopPropagation(); void takeOver(owner, sourceObjectId) }}
                  >
                    Take over
                  </Button>
                )}
              </CommandItem>
            )
          })}
        </CommandList>
      </CommandDialog>
    </section>
  )
}
