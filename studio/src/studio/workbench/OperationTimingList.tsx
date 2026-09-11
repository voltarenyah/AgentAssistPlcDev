import { useEffect, useState } from 'react'
import { CheckCircle2, Clock3, Loader2 } from 'lucide-react'
import type { OperationPhaseTiming, OperationStatus } from '@/api/client'

type Props = {
  status: OperationStatus | null
  className?: string
  layout?: 'inline' | 'dashboard'
}

export const formatElapsed = (elapsedMilliseconds: number) => {
  if (elapsedMilliseconds < 1000) return `${elapsedMilliseconds} ms`
  const seconds = elapsedMilliseconds / 1000
  if (seconds < 60) return `${seconds.toFixed(seconds < 10 ? 1 : 0)} s`
  const minutes = Math.floor(seconds / 60)
  return `${minutes} min ${Math.floor(seconds % 60)} s`
}

const CompletedPhase = ({ phase }: { phase: OperationPhaseTiming }) => (
  <li className="flex items-start gap-1.5 text-muted-foreground">
    <CheckCircle2 className="mt-0.5 h-3 w-3 shrink-0 text-emerald-500" aria-hidden="true" />
    <span className="min-w-0 flex-1 break-words">{phase.message}</span>
    <time className="shrink-0 font-mono text-foreground/80" dateTime={phase.completedAt ?? undefined}>{formatElapsed(phase.elapsedMilliseconds)}</time>
  </li>
)

const ActivePhase = ({ phase }: { phase: OperationPhaseTiming }) => {
  const [sample, setSample] = useState({ phase, elapsedMilliseconds: phase.elapsedMilliseconds })

  useEffect(() => {
    // Continue the server's measured duration locally while status requests are delayed.
    // Use a monotonic clock so wall-clock adjustments cannot distort the live timer.
    const receivedAt = performance.now()
    const timer = window.setInterval(() => {
      setSample({
        phase,
        elapsedMilliseconds: phase.elapsedMilliseconds + Math.floor(performance.now() - receivedAt),
      })
    }, 250)
    return () => window.clearInterval(timer)
  }, [phase])

  const elapsedMilliseconds = sample.phase === phase ? sample.elapsedMilliseconds : phase.elapsedMilliseconds
  return (
    <li className="flex items-start gap-1.5 text-foreground" aria-live="polite">
      <Loader2 className="mt-0.5 h-3 w-3 shrink-0 animate-spin text-chart-2" aria-hidden="true" />
      <span className="min-w-0 flex-1 break-words">{phase.message}</span>
      <time className="shrink-0 font-mono text-chart-2">{formatElapsed(elapsedMilliseconds)}</time>
    </li>
  )
}

const SourceExportPageSize = 100

const isSourceObjectActivity = (message: string) => /^(?:Exporting|Skipping)(?: fail-safe)? (?:source )?(?:block|UDT|tag table)\b/i.test(message)

const PhaseList = ({ phases, current, scrollable = false, currentFirst = false, onReachEnd }: {
  phases: OperationPhaseTiming[]
  current?: OperationPhaseTiming | null
  scrollable?: boolean
  currentFirst?: boolean
  onReachEnd?: () => void
}) => (
  <ol
    className={`${scrollable ? 'scrollbar-sleek min-h-0 flex-1 overflow-y-auto pr-1' : ''} space-y-1 text-[9px] leading-4`.trim()}
    onScroll={onReachEnd ? event => {
      const list = event.currentTarget
      if (list.scrollTop + list.clientHeight >= list.scrollHeight - 8) onReachEnd()
    } : undefined}
  >
    {currentFirst && current && <ActivePhase phase={current} />}
    {phases.map(phase => <CompletedPhase key={`${phase.startedAt}:${phase.message}`} phase={phase} />)}
    {!currentFirst && current && <ActivePhase phase={current} />}
  </ol>
)

const SourceExportActivity = ({
  phases,
  current,
  operationId,
}: {
  phases: OperationPhaseTiming[]
  current: OperationPhaseTiming | null
  operationId?: string
}) => {
  const [visibleRows, setVisibleRows] = useState(SourceExportPageSize)

  useEffect(() => {
    setVisibleRows(SourceExportPageSize)
  }, [operationId])

  const activeCount = current ? 1 : 0
  const visiblePhases = phases.slice(0, Math.max(0, visibleRows - activeCount))
  const totalRows = phases.length + activeCount
  const visibleCount = visiblePhases.length + activeCount
  const hasMore = visiblePhases.length < phases.length

  return (
    <>
      <PhaseList
        phases={visiblePhases}
        current={current}
        scrollable
        currentFirst
        onReachEnd={hasMore ? () => setVisibleRows(rows => Math.min(rows + SourceExportPageSize, totalRows)) : undefined}
      />
      <p className="mt-2 shrink-0 text-[9px] text-muted-foreground">
        {hasMore
          ? `Showing latest ${visibleCount} of ${totalRows}. Scroll down to load ${Math.min(SourceExportPageSize, totalRows - visibleCount)} more.`
          : `Showing all ${totalRows} source export items.`}
      </p>
    </>
  )
}

/** Shows the timeline carried by the existing operation-status polling contract.
 * Export counters update the active row in place; completed phases remain available
 * until the operation is dismissed by the user or expires server-side. */
export default function OperationTimingList({ status, className = '', layout = 'inline' }: Props) {
  const completed = status?.completedPhases ?? []
  const current = status?.currentPhase ?? null
  if (completed.length === 0 && current === null && layout === 'inline') return null

  const completedSourceExportRows = completed.filter(phase => isSourceObjectActivity(phase.message)).reverse()
  const activeSourceExportRow = current && isSourceObjectActivity(current.message) ? current : null
  const regularRows = completed.filter(phase => !isSourceObjectActivity(phase.message)).reverse()
  const activeRegularRow = activeSourceExportRow ? null : current

  if (layout === 'dashboard') {
    const hasSourceExportActivity = completedSourceExportRows.length > 0 || activeSourceExportRow !== null
    return (
      <section className={`flex min-h-0 flex-col rounded-lg border border-border/70 bg-background/55 ${className}`.trim()} data-operation-timings aria-label="Operation task timings">
        <div className="flex items-center gap-2 border-b border-border/70 px-4 py-3">
          <Clock3 className="h-4 w-4 text-chart-2" aria-hidden="true" />
          <div>
            <div className="text-xs font-semibold">Workflow timing</div>
            <p className="text-[10px] text-muted-foreground">Completed stages stay visible while the current task updates live.</p>
          </div>
        </div>
        <div className={`grid min-h-0 flex-1 gap-3 p-3 ${hasSourceExportActivity ? 'lg:grid-cols-[minmax(0,0.85fr)_minmax(0,1.15fr)]' : ''}`}>
          <section className="flex min-h-0 flex-col rounded-md border border-border/60 bg-muted/20 p-3" aria-label="Workflow stages">
            <div className="mb-2 text-[9px] font-semibold uppercase tracking-[0.14em] text-muted-foreground">Workflow stages</div>
            {regularRows.length > 0 || activeRegularRow ? (
              <PhaseList phases={regularRows} current={activeRegularRow} scrollable currentFirst />
            ) : (
              <div className="flex items-center gap-2 text-[10px] text-muted-foreground" aria-live="polite">
                <Loader2 className="h-3.5 w-3.5 animate-spin text-chart-2" aria-hidden="true" /> Connecting workflow telemetry…
              </div>
            )}
          </section>
          {hasSourceExportActivity && (
            <section className="flex min-h-0 flex-col rounded-md border border-chart-2/25 bg-chart-2/5 p-3" data-source-export-activity aria-label="Source export activity">
              <div className="mb-2 flex items-center justify-between gap-2">
                <div className="text-[9px] font-semibold uppercase tracking-[0.14em] text-muted-foreground">Source export activity</div>
                <span className="text-[9px] text-muted-foreground">Latest first</span>
              </div>
              <SourceExportActivity
                phases={completedSourceExportRows}
                current={activeSourceExportRow}
                operationId={status?.operationId}
              />
            </section>
          )}
        </div>
      </section>
    )
  }

  return (
    <section className={`rounded-md border border-border/70 bg-background/45 px-2.5 py-2 ${className}`.trim()} data-operation-timings aria-label="Operation task timings">
      <div className="mb-1 flex items-center gap-1.5 text-[8px] font-semibold uppercase tracking-[0.14em] text-muted-foreground">
        <Clock3 className="h-3 w-3" aria-hidden="true" /> Task timings
      </div>
      <PhaseList phases={completed} current={current} />
    </section>
  )
}
