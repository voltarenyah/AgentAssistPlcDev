import { useCallback, useEffect, useMemo, useState } from 'react'
import { AlertCircle, ChevronRight, Loader2 } from 'lucide-react'
import * as api from '@/api/client'
import type { SourceObjectInfo, VcCommitEntry } from '@/api/client'
import { Button } from '@/components/ui/button'

/**
 * The task page's Commits section: the task's commit edges, with the changed PLC source objects of
 * one commit behind a disclosure.
 *
 * The commit list itself comes from the task's traceability edges (`detail.commits`), so it renders
 * even when an auxiliary read fails. The message, author and timestamp come from the worktree's Git
 * history; a commit outside the bounded window states that its message is unavailable instead of
 * rendering a blank row. Expanding a commit reads the graph entity for that commit — its outgoing
 * `source_object` evidence edges and the changed files no source object could be resolved for — and
 * resolves name and category from the device's exported manifest. Nothing is backfilled: whatever
 * evidence is missing is named, not invented.
 */

/** The task's commit edge, as `EngineeringTaskDetail.commits` carries it. */
export type TaskCommitEdge = { id: string; edgeId: string; provenance: string; isPrimary: boolean }

type Props = {
  workbenchId: string
  /** Absent for a project-scope task, which has no Git history of its own. */
  worktreeId: string | null
  commits: TaskCommitEdge[]
  onNavigate?: (kind: string, id: string) => void
  onRemove?: (kind: string, item: TaskCommitEdge) => void
}

type CommitEvidence = { sourceObjects: TaskCommitEdge[]; unresolvedFiles: string[] }

/** `vc_log` caps `maxCount` at 100; older commits simply have no row to read a message from. */
const COMMIT_HISTORY_LIMIT = 100

const shortSha = (sha: string) => sha.slice(0, 7)
const errorMessage = (error: unknown) => (error instanceof Error ? error.message : String(error))

const provenanceLabel = (value: string) => {
  const normalized = value.toLowerCase()
  if (normalized === 'manual') return 'Manual link'
  if (normalized === 'evidence') return 'Evidence-derived'
  if (normalized === 'default') return 'Default link'
  return 'Unassigned'
}

const formatTimestamp = (value: string | undefined) => {
  if (!value) return null
  const parsed = new Date(value)
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString()
}

/** The commit's message/author/timestamp row, or null when the bounded history did not include it. */
const summaryFor = (summaries: Map<string, VcCommitEntry>, sha: string) =>
  summaries.get(sha) ?? summaries.get(sha.toLowerCase()) ?? null

export default function TaskCommitsSection({ workbenchId, worktreeId, commits, onNavigate, onRemove }: Props) {
  const [summaries, setSummaries] = useState<Map<string, VcCommitEntry>>(new Map())
  const [historyNote, setHistoryNote] = useState<string | null>(null)
  const [sourceInfo, setSourceInfo] = useState<Map<string, SourceObjectInfo>>(new Map())
  const [open, setOpen] = useState<Set<string>>(new Set())
  const [evidence, setEvidence] = useState<Record<string, CommitEvidence>>({})
  const [loadingEvidence, setLoadingEvidence] = useState<Record<string, boolean>>({})
  const [evidenceErrors, setEvidenceErrors] = useState<Record<string, string>>({})

  // The commit list is the task's own edges, so git metadata and manifest names are display
  // enrichment only: neither failure may hide the commits themselves.
  useEffect(() => {
    let cancelled = false
    setSummaries(new Map())
    setHistoryNote(null)
    setSourceInfo(new Map())
    if (!worktreeId) return
    void (async () => {
      const [history, devices] = await Promise.allSettled([
        api.getVersionControlWorktreeLog(workbenchId, worktreeId, COMMIT_HISTORY_LIMIT),
        api.listDevices(workbenchId, worktreeId),
      ])
      if (cancelled) return
      if (history.status === 'fulfilled') {
        setSummaries(new Map(history.value.commits.map(entry => [entry.sha, entry])))
      } else {
        setHistoryNote(`Commit messages and authors could not be read from the worktree history: ${errorMessage(history.reason)}`)
      }
      if (devices.status === 'rejected') return
      const snapshots = await Promise.allSettled(
        devices.value.map(device => api.getDeviceInfo(workbenchId, worktreeId, device.deviceId)))
      if (cancelled) return
      const byIdentity = new Map<string, SourceObjectInfo>()
      snapshots.forEach((result, index) => {
        if (result.status !== 'fulfilled') return
        const deviceId = devices.value[index]?.deviceId
        for (const source of result.value.sourceObjects) byIdentity.set(`${deviceId}:${source.id}`, source)
      })
      setSourceInfo(byIdentity)
    })()
    return () => { cancelled = true }
  }, [workbenchId, worktreeId])

  const loadEvidence = useCallback(async (sha: string) => {
    setLoadingEvidence(previous => ({ ...previous, [sha]: true }))
    setEvidenceErrors(previous => { const next = { ...previous }; delete next[sha]; return next })
    try {
      const entity = await api.getGraphEntityDetail(workbenchId, 'git_commit', sha)
      setEvidence(previous => ({ ...previous, [sha]: { sourceObjects: entity.sourceObjects, unresolvedFiles: entity.unresolvedFiles } }))
    } catch (caught) {
      setEvidenceErrors(previous => ({ ...previous, [sha]: errorMessage(caught) }))
    } finally {
      setLoadingEvidence(previous => ({ ...previous, [sha]: false }))
    }
  }, [workbenchId])

  const toggle = (sha: string) => {
    const willOpen = !open.has(sha)
    setOpen(previous => {
      const next = new Set(previous)
      if (next.has(sha)) next.delete(sha)
      else next.add(sha)
      return next
    })
    if (willOpen && !evidence[sha]) void loadEvidence(sha)
  }

  const ordered = useMemo(() => commits.map(commit => ({
    commit,
    short: shortSha(commit.id),
    summary: summaryFor(summaries, commit.id),
  })), [commits, summaries])

  return (
    <section className="overflow-hidden rounded-lg border bg-card" aria-label="Commits">
      <header className="flex items-center border-b px-4 py-3">
        <h3 className="text-sm font-semibold">Commits</h3>
        <span className="ml-auto rounded bg-muted px-2 py-1 text-xs text-muted-foreground">{commits.length}</span>
      </header>
      {commits.length === 0 ? (
        <p className="px-4 py-4 text-sm text-muted-foreground">No linked commits yet.</p>
      ) : (
        <>
          {historyNote && <p className="border-b px-4 py-2 text-xs text-muted-foreground" role="status">{historyNote}</p>}
          <ul className="divide-y divide-border">
            {ordered.map(({ commit, short, summary }) => {
              const expanded = open.has(commit.id)
              const detail = evidence[commit.id]
              const evidenceError = evidenceErrors[commit.id]
              const unresolved = detail?.unresolvedFiles.length ?? 0
              return <li key={commit.id} className="min-w-0">
                <div className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1 px-4 py-3">
                  {/* The commit is a disclosure, not a destination: no navigation handler can
                      resolve a commit on this page, so it is never rendered as a link to nowhere. */}
                  <button
                    type="button"
                    className="flex min-w-0 flex-1 items-center gap-2 text-left"
                    aria-expanded={expanded}
                    aria-label={`${expanded ? 'Hide' : 'Show'} details for commit ${short}`}
                    onClick={() => toggle(commit.id)}
                  >
                    <ChevronRight className={`h-3.5 w-3.5 shrink-0 text-muted-foreground transition-transform ${expanded ? 'rotate-90' : ''}`} />
                    <span className="shrink-0 font-mono text-sm">{short}</span>
                    <span className={`min-w-0 flex-1 truncate text-sm ${summary ? '' : 'text-muted-foreground'}`}>
                      {summary?.message || 'Message unavailable: this commit is not in the worktree history this page read.'}
                    </span>
                  </button>
                  {commit.isPrimary && <span className="shrink-0 rounded bg-muted px-2 py-1 text-xs">Primary</span>}
                  <span className="shrink-0 text-xs text-muted-foreground">{provenanceLabel(commit.provenance)}</span>
                  {onRemove && commit.provenance.toLowerCase() === 'manual' && (
                    <Button type="button" variant="outline" size="xs" aria-label={`Remove Commits ${commit.id}`} onClick={() => onRemove('commit', commit)}>Remove</Button>
                  )}
                </div>
                {expanded && (
                  <div className="space-y-2 border-t px-4 py-3">
                    <p className="text-xs text-muted-foreground">
                      {summary
                        ? `${summary.author || 'Unknown author'} · ${formatTimestamp(summary.timestamp) ?? 'Timestamp unavailable'}`
                        : 'Author and timestamp unavailable: this commit is not in the worktree history this page read.'}
                    </p>
                    {loadingEvidence[commit.id] ? (
                      <p className="flex items-center gap-2 text-xs text-muted-foreground" role="status">
                        <Loader2 className="h-3 w-3 animate-spin" /> Loading the commit&apos;s source objects...
                      </p>
                    ) : evidenceError ? (
                      <div className="flex items-center gap-2 text-xs text-muted-foreground" role="alert">
                        <AlertCircle className="h-3 w-3 shrink-0 text-red-500" />
                        <span className="min-w-0 flex-1">This commit&apos;s source objects could not be read: {evidenceError}</span>
                        <Button type="button" variant="outline" size="xs" onClick={() => void loadEvidence(commit.id)}>Retry</Button>
                      </div>
                    ) : detail ? (
                      <>
                        {detail.sourceObjects.length === 0 ? (
                          <p className="text-xs text-muted-foreground" data-testid="commit-no-source-objects">
                            No source objects: none of this commit&apos;s changed files resolved to a PLC source object.
                          </p>
                        ) : (
                          <ul className="space-y-1">
                            {detail.sourceObjects.map(item => {
                              const info = sourceInfo.get(item.id)
                              const name = info?.name ?? item.id
                              return <li key={item.edgeId} className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1">
                                <span className="shrink-0 rounded bg-muted px-2 py-1 text-xs text-muted-foreground">{info?.category ?? 'Category unavailable'}</span>
                                {onNavigate ? (
                                  <button
                                    type="button"
                                    className="min-w-0 flex-1 truncate text-left text-sm underline-offset-2 hover:underline focus-visible:underline"
                                    aria-label={`Open source object ${name} from commit ${short}`}
                                    onClick={() => onNavigate('sourceObject', item.id)}
                                  >{name}</button>
                                ) : <span className="min-w-0 flex-1 truncate text-sm">{name}</span>}
                                <span className="shrink-0 text-xs text-muted-foreground">{provenanceLabel(item.provenance)}</span>
                              </li>
                            })}
                          </ul>
                        )}
                        {unresolved > 0 && (
                          <p className="text-xs text-muted-foreground" data-testid="commit-unresolved-files">
                            {unresolved} changed {unresolved === 1 ? 'file' : 'files'} could not be resolved to a source object.
                          </p>
                        )}
                      </>
                    ) : null}
                  </div>
                )}
              </li>
            })}
          </ul>
        </>
      )}
    </section>
  )
}
