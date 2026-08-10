import { useCallback, useEffect, useMemo, useState } from 'react'
import { AlertCircle, GitBranch, Loader2, RefreshCw } from 'lucide-react'
import * as api from '@/api/client'
import { buildTimelineColumns, type TimelineColumn } from './versionControlTimeline'

const PAGE_SIZE = 10

type Props = {
  workbenchId: string
  worktreeId: string
}

type ActiveEvent =
  | { kind: 'git'; event: api.VersionControlTimelineGitCommit }
  | { kind: 'svn'; event: api.VersionControlTimelineSvnRevision }

const displayError = (error: unknown) =>
  error instanceof api.WorkbenchApiError
    ? `${error.code}: ${error.message}`
    : error instanceof Error ? error.message : 'Version-control history could not be loaded'

const shortValue = (value: string | null, length = 12) =>
  value ? `${value.slice(0, length)}${value.length > length ? '…' : ''}` : '—'

const formatDate = (value: string) => {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value || 'Unknown time' : date.toLocaleString()
}

function GitShape({
  event,
  onActivate,
}: {
  event: api.VersionControlTimelineGitCommit
  onActivate: (active: ActiveEvent | null) => void
}) {
  return (
    <button
      type="button"
      data-timeline-git={event.sha}
      aria-label={`Git commit ${event.sha}: ${event.message}`}
      className="group flex min-w-[108px] flex-col items-center rounded-md px-1 py-1 text-center outline-none focus-visible:ring-2 focus-visible:ring-ring"
      onMouseEnter={() => onActivate({ kind: 'git', event })}
      onMouseLeave={() => onActivate(null)}
      onFocus={() => onActivate({ kind: 'git', event })}
      onBlur={() => onActivate(null)}
    >
      <span className="grid h-3.5 w-3.5 place-items-center rounded-full border-2 border-card bg-chart-3 shadow-[0_0_0_1px_var(--chart-3)] transition-transform group-hover:scale-125 group-focus-visible:scale-125" />
      <span className="mt-2 max-w-[108px] truncate font-mono text-[9px] text-chart-3">{shortValue(event.sha, 9)}</span>
      <span className="mt-0.5 max-w-[108px] truncate font-mono text-[8px] text-muted-foreground">TIA {shortValue(event.tiaChecksum, 12)}</span>
    </button>
  )
}

function SvnShape({
  event,
  onActivate,
}: {
  event: api.VersionControlTimelineSvnRevision
  onActivate: (active: ActiveEvent | null) => void
}) {
  return (
    <button
      type="button"
      data-timeline-svn={String(event.revision)}
      aria-label={`SVN revision ${event.revision}: ${event.message}`}
      className="group flex min-w-[108px] flex-col items-center rounded-md px-1 py-1 text-center outline-none focus-visible:ring-2 focus-visible:ring-ring"
      onMouseEnter={() => onActivate({ kind: 'svn', event })}
      onMouseLeave={() => onActivate(null)}
      onFocus={() => onActivate({ kind: 'svn', event })}
      onBlur={() => onActivate(null)}
    >
      <span className="grid h-3.5 w-3.5 place-items-center rounded-[3px] border-2 border-card bg-chart-2 shadow-[0_0_0_1px_var(--chart-2)] transition-transform group-hover:scale-125 group-focus-visible:scale-125" />
      <span className="mt-2 max-w-[108px] truncate font-mono text-[9px] text-chart-2">r{event.revision}</span>
      <span className="mt-0.5 max-w-[108px] truncate font-mono text-[8px] text-muted-foreground">Git {shortValue(event.gitCommitSha, 9)}</span>
      <span className="max-w-[108px] truncate font-mono text-[8px] text-muted-foreground">TIA {shortValue(event.tiaChecksum, 12)}</span>
    </button>
  )
}

function TimelineColumnView({
  column,
  onActivate,
}: {
  column: TimelineColumn
  onActivate: (active: ActiveEvent | null) => void
}) {
  const linkId = column.svn ? `${column.git.sha}-r${column.svn.revision}` : undefined
  return (
    <div className="relative flex min-w-[124px] flex-1 flex-col items-center">
      <div className="relative z-10 flex h-24 items-start justify-center pt-2">
        <GitShape event={column.git} onActivate={onActivate} />
      </div>
      <div className="relative z-10 flex h-24 items-start justify-center pt-2">
        {column.svn && <SvnShape event={column.svn} onActivate={onActivate} />}
      </div>
      {column.svn && (
        <span
          aria-hidden="true"
          data-timeline-link={linkId}
          className="pointer-events-none absolute left-1/2 top-5 h-[76px] -translate-x-1/2 border-l-2 border-dashed border-amber-500/70"
        />
      )}
    </div>
  )
}

function EventDetails({ active }: { active: ActiveEvent }) {
  const isGit = active.kind === 'git'
  const event = active.event
  return (
    <div className="mx-4 mb-3 rounded-lg border bg-muted/40 p-3 text-[9px]" style={{ borderColor: 'var(--border)' }}>
      <div className="flex items-center gap-2">
        <span className={`h-2.5 w-2.5 ${isGit ? 'rounded-full bg-chart-3' : 'rounded-[2px] bg-chart-2'}`} />
        <span className="font-semibold">{isGit ? 'Git commit' : 'SVN revision'}</span>
        <span className="font-mono text-muted-foreground">
          {isGit ? event.sha : `r${event.revision}`}
        </span>
      </div>
      <p className="mt-2 font-medium">{event.message || 'No commit message'}</p>
      <div className="mt-1 grid grid-cols-1 gap-x-4 gap-y-1 text-muted-foreground sm:grid-cols-2">
        <span>Author: {event.author || 'Unknown author'}</span>
        <span>Time: {formatDate(event.timestamp)}</span>
        <span>TIA checksum: {event.tiaChecksum ?? '—'}</span>
        {!isGit && <span>Git commit: {event.gitCommitSha}</span>}
        {isGit && event.files.length > 0 && <span>Changed files: {event.files.length}</span>}
      </div>
    </div>
  )
}

export default function WorktreeVersionControlTimeline({ workbenchId, worktreeId }: Props) {
  const [gitCommits, setGitCommits] = useState<api.VersionControlTimelineGitCommit[]>([])
  const [svnRevisions, setSvnRevisions] = useState<api.VersionControlTimelineSvnRevision[]>([])
  const [hasMore, setHasMore] = useState(false)
  const [loading, setLoading] = useState(true)
  const [loadingMore, setLoadingMore] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [activeEvent, setActiveEvent] = useState<ActiveEvent | null>(null)

  const loadPage = useCallback(async (offset: number, append: boolean) => {
    if (append) setLoadingMore(true)
    else setLoading(true)
    setError(null)
    try {
      const result = await api.getWorktreeVersionControlTimeline(workbenchId, worktreeId, offset, PAGE_SIZE)
      if (append) {
        setGitCommits(previous => {
          const known = new Set(previous.map(commit => commit.sha))
          return [...previous, ...result.gitCommits.filter(commit => !known.has(commit.sha))]
        })
        setSvnRevisions(previous => {
          const known = new Set(previous.map(revision => revision.revision))
          return [...previous, ...result.svnRevisions.filter(revision => !known.has(revision.revision))]
        })
      } else {
        setGitCommits(result.gitCommits)
        setSvnRevisions(result.svnRevisions)
      }
      setHasMore(result.hasMore)
    } catch (reason) {
      setError(displayError(reason))
    } finally {
      setLoading(false)
      setLoadingMore(false)
    }
  }, [workbenchId, worktreeId])

  useEffect(() => {
    setGitCommits([])
    setSvnRevisions([])
    void loadPage(0, false)
  }, [loadPage])

  const columns = useMemo(
    () => buildTimelineColumns({ gitCommits, svnRevisions, hasMore }),
    [gitCommits, svnRevisions, hasMore],
  )

  return (
    <section className="overflow-hidden rounded-xl border bg-card" aria-label="Worktree version control" style={{ borderColor: 'var(--border)' }}>
      <header className="flex items-center gap-3 border-b px-4 py-3" style={{ borderColor: 'var(--border)' }}>
        <GitBranch className="h-4 w-4 text-chart-4" />
        <div className="min-w-0 flex-1">
          <h2 className="text-sm font-semibold">Worktree version control</h2>
          <p className="mt-0.5 text-[9px] text-muted-foreground">Git commits and linked SVN savepoints · newest first</p>
        </div>
        {!loading && gitCommits.length > 0 && <span className="text-[9px] text-muted-foreground">{gitCommits.length} loaded</span>}
      </header>

      {loading && (
        <div className="flex items-center justify-center gap-2 p-7 text-[10px] text-muted-foreground">
          <Loader2 className="h-3.5 w-3.5 animate-spin" /> Loading version-control history...
        </div>
      )}

      {!loading && error && gitCommits.length === 0 && (
        <div className="flex items-center gap-3 p-5 text-[10px] text-muted-foreground">
          <AlertCircle className="h-4 w-4 shrink-0 text-red-500" />
          <span className="min-w-0 flex-1">{error}</span>
          <button type="button" data-testid="timeline-retry" className="secondary-button h-7 text-[9px]" onClick={() => void loadPage(0, false)}>
            <RefreshCw className="mr-1 inline h-3 w-3" /> Retry
          </button>
        </div>
      )}

      {!loading && !error && columns.length === 0 && (
        <div className="p-7 text-center text-[10px] text-muted-foreground">No version-control history yet.</div>
      )}

      {!loading && columns.length > 0 && (
        <>
          <div className="overflow-x-auto px-4 pb-3 pt-4">
            <div className="min-w-[640px]">
              <div className="flex">
                <div className="w-11 shrink-0">
                  <div className="flex h-24 items-start pt-2 text-[9px] font-semibold uppercase tracking-[0.12em] text-chart-3">Git</div>
                  <div className="flex h-24 items-start pt-2 text-[9px] font-semibold uppercase tracking-[0.12em] text-chart-2">SVN</div>
                </div>
                <div className="relative min-w-0 flex-1">
                  <span aria-hidden="true" className="pointer-events-none absolute left-0 right-0 top-8 border-t border-chart-3/35" />
                  <span aria-hidden="true" className="pointer-events-none absolute bottom-8 left-0 right-0 border-t border-chart-2/35" />
                  <div className="relative flex">{columns.map(column => (
                    <TimelineColumnView key={column.git.sha} column={column} onActivate={setActiveEvent} />
                  ))}</div>
                </div>
              </div>
              <div className="mt-2 flex items-center gap-4 pl-11 text-[8px] text-muted-foreground">
                <span className="inline-flex items-center gap-1"><i className="h-2 w-2 rounded-full bg-chart-3" /> Git commit</span>
                <span className="inline-flex items-center gap-1"><i className="h-2 w-2 rounded-[2px] bg-chart-2" /> SVN revision</span>
                <span className="inline-flex items-center gap-1"><i className="w-4 border-t border-dashed border-amber-500" /> linked savepoint</span>
              </div>
            </div>
          </div>
          {activeEvent && <EventDetails active={activeEvent} />}
          <div className="flex items-center justify-between border-t px-4 py-2.5" style={{ borderColor: 'var(--border)' }}>
            <span className="text-[9px] text-muted-foreground">Hover or focus a shape for commit details.</span>
            {hasMore && (
              <button
                type="button"
                data-testid="timeline-load-more"
                className="secondary-button h-7 text-[9px]"
                disabled={loadingMore}
                onClick={() => void loadPage(gitCommits.length, true)}
              >
                {loadingMore && <Loader2 className="mr-1 inline h-3 w-3 animate-spin" />}
                {loadingMore ? 'Loading…' : 'Load more'}
              </button>
            )}
          </div>
        </>
      )}
    </section>
  )
}
