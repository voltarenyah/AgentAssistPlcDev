import { useCallback, useEffect, useMemo, useState } from 'react'
import { AlertCircle, Boxes, GitBranch, Loader2, RefreshCw, Sparkles } from 'lucide-react'
import * as api from '@/api/client'
import { showErrorToast } from '@/components/ui/toast'
import InlineEdit from './InlineEdit'
import StatusBadge from './StatusBadge'

type Props = {
  workbenchId: string
  onSelectWorktree: (worktreeId: string) => void
  onOpenAssistant?: () => void
}

const displayError = (error: unknown) => {
  if (error instanceof api.WorkbenchApiError) return `${error.code}: ${error.message}`
  return error instanceof Error ? error.message : 'Unexpected operation failure'
}

const formatDate = (value: string | null) =>
  value ? new Date(value).toLocaleString() : '—'

const orderWorktrees = (worktrees: api.WorktreeOverview[]) => {
  const ongoing = worktrees.filter(worktree => worktree.status !== 'finished')
  const finished = worktrees
    .filter(worktree => worktree.status === 'finished')
    .sort((a, b) => (b.finishedUtc ?? '').localeCompare(a.finishedUtc ?? ''))
  return [...ongoing, ...finished]
}

export default function ProjectLandingPage({ workbenchId, onSelectWorktree, onOpenAssistant }: Props) {
  const [overview, setOverview] = useState<api.WorkbenchOverview | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const reload = useCallback(async () => {
    try {
      const result = await api.getWorkbenchOverview(workbenchId)
      setOverview(result)
      setError(null)
      return true
    } catch (loadError) {
      setError(displayError(loadError))
      return false
    } finally {
      setLoading(false)
    }
  }, [workbenchId])

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    setError(null)
    void api.getWorkbenchOverview(workbenchId)
      .then(result => {
        if (cancelled) return
        setOverview(result)
        setLoading(false)
      })
      .catch(loadError => {
        if (cancelled) return
        setOverview(null)
        setError(displayError(loadError))
        setLoading(false)
      })
    return () => { cancelled = true }
  }, [workbenchId])

  const orderedWorktrees = useMemo(
    () => (overview ? orderWorktrees(overview.worktrees) : []),
    [overview],
  )

  const saveWorkbenchField = async (patch: { purpose?: string; owner?: string }) => {
    try {
      await api.updateWorkbench(workbenchId, patch)
      await reload()
    } catch (saveError) {
      showErrorToast(`Project metadata could not be saved: ${displayError(saveError)}`)
    }
  }

  const changeWorktreeStatus = async (worktree: api.WorktreeOverview, status: api.WorktreeStatus) => {
    await api.updateWorktree(workbenchId, worktree.worktreeId, { status })
    await reload()
  }

  if (loading && !overview) {
    return (
      <div className="grid h-full min-h-[520px] place-items-center p-8">
        <div className="flex items-center gap-2 text-[10px] text-muted-foreground">
          <Loader2 className="h-4 w-4 animate-spin" /> Loading project overview...
        </div>
      </div>
    )
  }

  if (error && !overview) {
    return (
      <div className="grid h-full min-h-[520px] place-items-center p-8">
        <div className="max-w-md text-center">
          <div className="mx-auto mb-4 grid h-14 w-14 place-items-center rounded-2xl border bg-card shadow-sm" style={{ borderColor: 'var(--border)' }}>
            <AlertCircle className="h-7 w-7 text-red-500" />
          </div>
          <h2 className="text-base font-semibold">Project overview unavailable</h2>
          <p className="mt-2 text-[10px] leading-relaxed text-muted-foreground">{error}</p>
          <button className="secondary-button mt-4" onClick={() => { setLoading(true); void reload() }}>
            <RefreshCw className="h-3.5 w-3.5" /> Retry
          </button>
        </div>
      </div>
    )
  }

  if (!overview) return null

  return (
    <div className="scrollbar-sleek min-h-0 flex-1 overflow-y-auto">
      <div className="mx-auto max-w-6xl space-y-5 p-5">
        <section className="flex flex-wrap items-start gap-4 rounded-xl border bg-card p-5" style={{ borderColor: 'var(--border)' }}>
          <div className="grid h-12 w-12 place-items-center rounded-xl bg-chart-2/10">
            <Boxes className="h-5 w-5 text-chart-2" />
          </div>
          <div className="min-w-0 flex-1">
            <h1 className="text-lg font-semibold">{overview.name}</h1>
            <p className="mt-0.5 text-[9px] text-muted-foreground">Created {formatDate(overview.createdAt)}</p>
            <div className="mt-2 space-y-1 font-mono text-[9px] text-muted-foreground">
              <p className="break-all">Root: {overview.rootPath}</p>
              <p className="break-all">Source project: {overview.sourceProjectPath ?? '—'}</p>
            </div>
          </div>
          {onOpenAssistant && (
            <button
              className="secondary-button shrink-0"
              aria-label="Open Workbench Assistant"
              onClick={onOpenAssistant}
            >
              <Sparkles className="h-3.5 w-3.5" />
              Open Workbench Assistant
            </button>
          )}
          <div className="grid w-full max-w-sm grid-cols-[64px_1fr] items-center gap-x-3 gap-y-2">
            <span className="self-center text-[9px] uppercase tracking-wide text-muted-foreground">Purpose</span>
            <InlineEdit
              ariaLabel="Project purpose"
              placeholder="What is this project for?"
              value={overview.purpose ?? ''}
              onSave={purpose => saveWorkbenchField({ purpose })}
            />
            <span className="self-center text-[9px] uppercase tracking-wide text-muted-foreground">Owner</span>
            <InlineEdit
              ariaLabel="Project owner"
              placeholder="Responsible person"
              value={overview.owner ?? ''}
              onSave={owner => saveWorkbenchField({ owner })}
            />
          </div>
        </section>

        <section className="overflow-hidden rounded-xl border bg-card" style={{ borderColor: 'var(--border)' }}>
          <div className="flex items-center border-b px-4 py-3" style={{ borderColor: 'var(--border)' }}>
            <span className="text-[10px] font-semibold">Worktrees</span>
            <span className="ml-auto text-[9px] text-muted-foreground">
              {overview.worktrees.length} worktree{overview.worktrees.length === 1 ? '' : 's'}
            </span>
          </div>
          {orderedWorktrees.length === 0 ? (
            <div className="p-8 text-center text-[10px] text-muted-foreground">
              No worktrees yet. Create a linked worktree from the project tree to start working.
            </div>
          ) : (
            <table className="w-full text-[10px]">
              <thead className="sticky top-0 bg-card">
                <tr className="border-b text-left text-[9px] uppercase tracking-wide text-muted-foreground" style={{ borderColor: 'var(--border)' }}>
                  <th className="px-4 py-2 font-medium">Title</th>
                  <th className="px-4 py-2 font-medium">Branch</th>
                  <th className="px-4 py-2 font-medium">Status</th>
                  <th className="px-4 py-2 font-medium">Owner</th>
                  <th className="px-4 py-2 font-medium">Purpose</th>
                  <th className="px-4 py-2 font-medium">Tasks</th>
                </tr>
              </thead>
              <tbody>
                {orderedWorktrees.map(worktree => (
                  <tr
                    key={worktree.worktreeId}
                    className="cursor-pointer border-b hover:bg-accent/40"
                    style={{ borderColor: 'var(--border)' }}
                    onClick={() => onSelectWorktree(worktree.worktreeId)}
                  >
                    <td className="px-4 py-1.5 font-medium">{worktree.name}</td>
                    <td className="px-4 py-1.5">
                      <span className="inline-flex items-center gap-1 font-mono text-[9px] text-muted-foreground">
                        <GitBranch className="h-3 w-3" /> {worktree.branch}
                      </span>
                    </td>
                    <td className="px-4 py-1.5" onClick={event => event.stopPropagation()}>
                      <StatusBadge
                        status={worktree.status}
                        onChange={status => changeWorktreeStatus(worktree, status)}
                      />
                    </td>
                    <td className="px-4 py-1.5">{worktree.owner || '—'}</td>
                    <td className="max-w-[220px] truncate px-4 py-1.5 text-muted-foreground" title={worktree.purpose ?? undefined}>
                      {worktree.purpose || '—'}
                    </td>
                    <td className="px-4 py-1.5 font-mono text-[9px] text-muted-foreground">
                      {worktree.openTasks} / {worktree.totalTasks}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </section>
      </div>
    </div>
  )
}
