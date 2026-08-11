import { useCallback, useEffect, useMemo, useState, type FocusEvent, type MouseEvent } from 'react'
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

type DetailPosition = {
  left: number
  top: number
}

const displayError = (error: unknown) =>
  error instanceof api.WorkbenchApiError
    ? `${error.code}: ${error.message}`
    : error instanceof Error ? error.message : 'Version-control history could not be loaded'

const shortGitHash = (value: string) => value.length > 7 ? value.slice(0, 7) : value

const displayChecksum = (value: string | null) => {
  if (!value) return '—'
  const separator = value.indexOf(':')
  return separator >= 0 ? value.slice(separator + 1) : value
}

const formatTimestamp = (value: string) => {
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value || 'Unknown time'
  const pad = (part: number) => String(part).padStart(2, '0')
  return `${date.getFullYear()}/${date.getMonth() + 1}/${date.getDate()} ${pad(date.getHours())}:${pad(date.getMinutes())}`
}

const formatTimelineTimestamp = formatTimestamp

const pointerPosition = (event: MouseEvent<HTMLButtonElement>): DetailPosition => ({
  left: event.clientX + 16,
  top: event.clientY + 16,
})

const focusPosition = (event: FocusEvent<HTMLButtonElement>): DetailPosition => {
  const bounds = event.currentTarget.getBoundingClientRect()
  return { left: bounds.right + 12, top: bounds.top }
}

function GitShape({
  event,
  onActivate,
}: {
  event: api.VersionControlTimelineGitCommit
  onActivate: (active: ActiveEvent | null, position?: DetailPosition) => void
}) {
  return (
    <button
      type="button"
      data-timeline-git={event.sha}
      aria-label={`Git commit ${event.sha}: ${event.message}`}
      className="group flex h-16 w-16 flex-col items-center justify-start rounded-md pt-2 outline-none focus-visible:ring-2 focus-visible:ring-ring"
      onMouseEnter={mouseEvent => onActivate({ kind: 'git', event }, pointerPosition(mouseEvent))}
      onMouseMove={mouseEvent => onActivate({ kind: 'git', event }, pointerPosition(mouseEvent))}
      onMouseLeave={() => onActivate(null)}
      onFocus={focusEvent => onActivate({ kind: 'git', event }, focusPosition(focusEvent))}
      onBlur={() => onActivate(null)}
    >
      <span className="grid h-3.5 w-3.5 place-items-center rounded-full border-2 border-card bg-chart-3 shadow-[0_0_0_1px_var(--chart-3)] transition-transform group-hover:scale-125 group-focus-visible:scale-125" />
    </button>
  )
}

function SvnShape({
  event,
  onActivate,
}: {
  event: api.VersionControlTimelineSvnRevision
  onActivate: (active: ActiveEvent | null, position?: DetailPosition) => void
}) {
  return (
    <button
      type="button"
      data-timeline-svn={String(event.revision)}
      aria-label={`SVN revision ${event.revision}: ${event.message}`}
      className="group flex h-16 w-16 flex-col items-center justify-start rounded-md pt-2 outline-none focus-visible:ring-2 focus-visible:ring-ring"
      onMouseEnter={mouseEvent => onActivate({ kind: 'svn', event }, pointerPosition(mouseEvent))}
      onMouseMove={mouseEvent => onActivate({ kind: 'svn', event }, pointerPosition(mouseEvent))}
      onMouseLeave={() => onActivate(null)}
      onFocus={focusEvent => onActivate({ kind: 'svn', event }, focusPosition(focusEvent))}
      onBlur={() => onActivate(null)}
    >
      <span className="grid h-3.5 w-3.5 place-items-center rounded-[3px] border-2 border-card bg-chart-2 shadow-[0_0_0_1px_var(--chart-2)] transition-transform group-hover:scale-125 group-focus-visible:scale-125" />
      <span className="mt-1 font-mono text-[9px] text-chart-2">r{event.revision}</span>
    </button>
  )
}

function TimelineColumnView({
  column,
  onActivate,
}: {
  column: TimelineColumn
  onActivate: (active: ActiveEvent | null, position?: DetailPosition) => void
}) {
  const sharedChecksum = column.git.tiaChecksum ?? column.svn?.tiaChecksum ?? null
  const checksumText = displayChecksum(sharedChecksum)
  return (
    <div data-timeline-column className="relative flex min-w-[176px] flex-1 flex-col items-center rounded-lg border bg-muted/20 px-1 py-1" style={{ borderColor: 'var(--border)' }}>
      <div className="relative z-10 flex h-16 items-start justify-center pt-2">
        <GitShape event={column.git} onActivate={onActivate} />
      </div>
      <div className="relative z-10 flex h-7 w-full items-center justify-center px-2">
        <span
          data-timeline-git-hash={column.git.sha}
          title={column.git.sha}
          className="w-full break-all text-center font-mono text-[8px] leading-3 text-foreground/80"
        >
          {shortGitHash(column.git.sha)}
        </span>
      </div>
      <div className="relative z-10 flex h-7 w-full items-center justify-center px-2">
        <span
          data-timeline-timestamp={column.git.timestamp}
          title={column.git.timestamp}
          className="w-full break-all text-center text-[8px] leading-3 text-foreground/80"
        >
          {formatTimelineTimestamp(column.git.timestamp)}
        </span>
      </div>
      <div className="relative z-10 flex h-7 w-full items-center justify-center border-y px-2" style={{ borderColor: 'var(--border)' }}>
        <span
          data-timeline-tia-checksum={checksumText}
          title={sharedChecksum ?? undefined}
          className="w-full break-all text-center font-mono text-[8px] leading-3 text-foreground/80"
        >
          {checksumText}
        </span>
      </div>
      <div className="relative z-10 flex h-16 items-start justify-center pt-2">
        {column.svn && <SvnShape event={column.svn} onActivate={onActivate} />}
      </div>
    </div>
  )
}

function EventDetails({ active, position }: { active: ActiveEvent; position: DetailPosition }) {
  const isGit = active.kind === 'git'
  const event = active.event
  const identifier = 'sha' in event ? event.sha : `r${event.revision}`
  return (
    <div
      data-testid="timeline-event-details"
      className="pointer-events-none fixed z-50 w-[min(360px,calc(100vw-1rem))] rounded-lg border bg-muted/95 p-3 text-[9px] shadow-xl backdrop-blur-sm"
      style={{ left: `${position.left}px`, top: `${position.top}px`, borderColor: 'var(--border)' }}
    >
      <div className="flex items-center gap-2">
        <span className={`h-2.5 w-2.5 ${isGit ? 'rounded-full bg-chart-3' : 'rounded-[2px] bg-chart-2'}`} />
        <span className="font-semibold">{isGit ? 'Git commit' : 'SVN revision'}</span>
        <span className="font-mono text-muted-foreground">{identifier}</span>
      </div>
      <p className="mt-2 font-medium">{event.message || 'No commit message'}</p>
      <div className="mt-1 grid grid-cols-1 gap-x-4 gap-y-1 text-muted-foreground sm:grid-cols-2">
        <span>Author: {event.author || 'Unknown author'}</span>
        <span>Time: {formatTimestamp(event.timestamp)}</span>
        <span>TIA checksum: {event.tiaChecksum ?? '—'}</span>
        {!isGit && 'gitCommitSha' in event && <span title={event.gitCommitSha}>Git commit: {shortGitHash(event.gitCommitSha)}</span>}
        {isGit && 'files' in event && event.files.length > 0 && <span>Changed files: {event.files.length}</span>}
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
  const [detailPosition, setDetailPosition] = useState<DetailPosition | null>(null)

  const activateEvent = (active: ActiveEvent | null, position?: DetailPosition) => {
    setActiveEvent(active)
    setDetailPosition(position ?? null)
  }

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
    <section className="relative overflow-visible rounded-xl border bg-card" aria-label="Worktree version control" style={{ borderColor: 'var(--border)' }}>
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
          <div className="flex items-stretch px-4 pb-3 pt-4">
            <div data-testid="timeline-labels" className="w-24 shrink-0 border-r pr-3" style={{ borderColor: 'var(--border)' }}>
                  <div className="flex h-16 items-center text-[9px] font-semibold uppercase tracking-[0.12em] text-chart-3">Git</div>
                  <div className="flex h-7 items-center text-[8px] font-semibold text-muted-foreground">Git hash</div>
                  <div className="flex h-7 items-center text-[8px] font-semibold text-muted-foreground">Timestamp</div>
                  <div className="flex h-7 items-center text-[8px] font-semibold text-muted-foreground">TIA checksum</div>
                  <div className="flex h-16 items-center text-[9px] font-semibold uppercase tracking-[0.12em] text-chart-2">SVN</div>
            </div>
            <div data-testid="timeline-scroll" className="min-w-0 flex-1 overflow-x-auto pl-3">
              <div className="min-w-max">
                <div className="relative flex gap-2">{columns.map(column => (
                  <TimelineColumnView key={column.git.sha} column={column} onActivate={activateEvent} />
                ))}</div>
              </div>
            </div>
          </div>
          <div className="flex items-center gap-4 px-4 pb-3 pl-28 text-[8px] text-muted-foreground">
            <span className="inline-flex items-center gap-1"><i className="h-2 w-2 rounded-full bg-chart-3" /> Git commit</span>
            <span className="inline-flex items-center gap-1"><i className="h-2 w-2 rounded-[2px] bg-chart-2" /> SVN revision</span>
          </div>
          {activeEvent && detailPosition && <EventDetails active={activeEvent} position={detailPosition} />}
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
